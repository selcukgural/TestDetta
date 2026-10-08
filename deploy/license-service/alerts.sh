#!/bin/bash
# One-time (re-runnable): e-mails what needs a person from the license service's logs, through Cloud
# Monitoring log-based alerts (docs/design/licensing.md, "Alerts"). Usage: alerts.sh [production|sandbox] <address>.
# Reads no secret. The service logs one JSON line per entry with a Cloud Logging severity (CloudLogFormatter),
# so this needs a deploy from a commit that has it.
#  - Warnings and errors of the service and its jobs: trials paused at the daily ceiling, a license that needs
#    attention or was not e-mailed, Resend failures, rejected webhooks, unhandled exceptions. Only the
#    application's own lines count, not Cloud Run's request log, where every 404 of an unknown license is a warning.
#  - A trial refused because the address or an owner had one: usually a team asking twice, but also how someone
#    else's request would block an owner. Support can release the owner (admin.sh <env> trial-release --owner <host/owner> --note <why>).
# At most one mail per policy per hour.
set -euo pipefail
P=testdetta-510908

case "${1:-}" in
  production) SERVICE=license-service ;;
  sandbox) SERVICE=license-service-sandbox ;;
  *) echo "usage: alerts.sh [production|sandbox] <address>" >&2; exit 2 ;;
esac
ADDRESS="${2:?usage: alerts.sh [production|sandbox] <address>}"

gcloud services enable monitoring.googleapis.com --project $P

CHANNEL=$(gcloud beta monitoring channels list --project $P --filter "type=email AND labels.email_address=\"$ADDRESS\"" --format 'value(name)' | head -n 1)
if [ -z "$CHANNEL" ]; then
  CHANNEL=$(gcloud beta monitoring channels create --project $P --type email --display-name "License alerts: $ADDRESS" \
    --channel-labels "email_address=$ADDRESS" --format 'value(name)')
fi

SOURCE="(resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"$SERVICE\" OR resource.type=\"cloud_run_job\" AND (resource.labels.job_name=\"$SERVICE-trials\" OR resource.labels.job_name=\"$SERVICE-admin\")) AND (log_id(\"run.googleapis.com/stdout\") OR log_id(\"run.googleapis.com/stderr\"))"

# Creates the policy named $1 matching log filter $2, or replaces the one with that name.
policy() {
  local name="$1" filter="$2" file
  file=$(mktemp)
  NAME="$name" FILTER="$filter" CHANNEL="$CHANNEL" python3 -c '
import json, os
print(json.dumps({
  "displayName": os.environ["NAME"],
  "combiner": "OR",
  "conditions": [{"displayName": os.environ["NAME"], "conditionMatchedLog": {"filter": os.environ["FILTER"]}}],
  "alertStrategy": {"notificationRateLimit": {"period": "3600s"}, "autoClose": "1800s"},
  "notificationChannels": [os.environ["CHANNEL"]],
  "documentation": {"mimeType": "text/markdown", "content": "Read the matching lines in Logs Explorer (the link above). docs/design/licensing.md, \"Alerts\"."},
}))' > "$file"
  local existing
  existing=$(gcloud alpha monitoring policies list --project $P --filter "displayName=\"$name\"" --format 'value(name)' | head -n 1)
  if [ -n "$existing" ]; then
    gcloud alpha monitoring policies update "$existing" --project $P --policy-from-file "$file" > /dev/null
  else
    gcloud alpha monitoring policies create --project $P --policy-from-file "$file" > /dev/null
  fi
  rm -f "$file"
  echo "alert: $name -> $ADDRESS"
}

policy "$SERVICE: warnings and errors" "$SOURCE AND severity>=WARNING"
policy "$SERVICE: trial refused (address or owner had one)" "$SOURCE AND jsonPayload.message:\"Trial request: HadOne\""
