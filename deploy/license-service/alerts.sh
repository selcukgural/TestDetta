#!/bin/bash
# One-time (re-runnable): e-mails what needs a person from the license service's logs, through Cloud
# Monitoring log-based alerts (docs/design/licensing.md, "Logs and alerts"). Usage: alerts.sh [production|sandbox] <address>.
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

# Every group in parentheses: in the Logging query language OR binds tighter than AND.
SOURCE="((resource.type=\"cloud_run_revision\" AND resource.labels.service_name=\"$SERVICE\") OR (resource.type=\"cloud_run_job\" AND (resource.labels.job_name=\"$SERVICE-trials\" OR resource.labels.job_name=\"$SERVICE-admin\"))) AND (log_id(\"run.googleapis.com/stdout\") OR log_id(\"run.googleapis.com/stderr\"))"

# The Monitoring REST API directly: gcloud's channel and policy commands are beta and alpha components, which a
# fresh gcloud install lacks and installs only interactively.
TOKEN=$(gcloud auth print-access-token) PROJECT=$P SERVICE=$SERVICE ADDRESS="$ADDRESS" SOURCE="$SOURCE" python3 - <<'EOF'
import json, os, urllib.parse, urllib.request

api = "https://monitoring.googleapis.com/v3/projects/" + os.environ["PROJECT"]
headers = {"Authorization": "Bearer " + os.environ["TOKEN"], "Content-Type": "application/json"}
service, address, source = os.environ["SERVICE"], os.environ["ADDRESS"], os.environ["SOURCE"]

def call(method, url, body=None):
    request = urllib.request.Request(url, method=method, headers=headers, data=None if body is None else json.dumps(body).encode())
    with urllib.request.urlopen(request) as response:
        return json.load(response)

def listed(kind, filter):
    return call("GET", f"{api}/{kind}?" + urllib.parse.urlencode({"filter": filter})).get(kind, [])

channels = listed("notificationChannels", f'type="email" AND labels.email_address="{address}"')
channel = channels[0]["name"] if channels else call("POST", f"{api}/notificationChannels", {
    "type": "email", "displayName": f"License alerts: {address}", "labels": {"email_address": address}})["name"]

def policy(name, filter):
    body = {
        "displayName": name,
        "combiner": "OR",
        "conditions": [{"displayName": name, "conditionMatchedLog": {"filter": filter}}],
        "alertStrategy": {"notificationRateLimit": {"period": "3600s"}, "autoClose": "1800s"},
        "notificationChannels": [channel],
        "documentation": {"mimeType": "text/markdown", "content": "Read the matching lines in Logs Explorer (the link above). docs/design/licensing.md, \"Logs and alerts\"."},
    }
    existing = listed("alertPolicies", f'display_name="{name}"')
    if existing:
        call("PATCH", "https://monitoring.googleapis.com/v3/" + existing[0]["name"], body)
    else:
        call("POST", f"{api}/alertPolicies", body)
    print(f"alert: {name} -> {address}")

policy(f"{service}: warnings and errors", f"{source} AND severity>=WARNING")
policy(f"{service}: trial refused (address or owner had one)", f'{source} AND jsonPayload.message:"Trial request: HadOne"')
EOF
