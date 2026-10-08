#!/bin/bash
# Runs one admin command of the license service (AdminCommand: issue, update, revoke, restore, send, show,
# attention, list) in production or the sandbox, where there is no shell to run it from. deploy.sh deploys the
# Cloud Run job <service>-admin from the same image, with the same database, signing key and Resend key; this
# script runs it with the given arguments, waits, prints what it wrote and deletes the execution.
#   admin.sh production attention
#   admin.sh production show --id P-<subscription id>
#   admin.sh production update --id P-<subscription id> --scope github.com/acme --note "typo at checkout, by mail"
# What the command prints (license strings for show, issue and update) lands in the project's own logs, which only
# the owner reads. The arguments are kept in the project's Admin Activity audit log (400 days), so an address given
# to `send --to` stays there; to keep none, send the string from `show` by hand instead.
set -euo pipefail
P=testdetta-510908 R=europe-west1

usage() { echo "usage: admin.sh production|sandbox <admin command> [options]   (see AdminCommand.Usage)" >&2; exit 2; }
case "${1:-}" in
  production) JOB=license-service-admin ;;
  sandbox) JOB=license-service-sandbox-admin ;;
  *) usage ;;
esac
shift
[ $# -gt 0 ] || usage

# gcloud splits --args at commas; a delimiter of our own keeps "Acme, Inc." in one argument.
D='|~|'
ARGS=admin
for a in "$@"; do
  case "$a" in *"$D"*) echo "admin.sh: an argument contains $D" >&2; exit 2 ;; esac
  ARGS="$ARGS$D$a"
done

EXEC=$(gcloud run jobs execute "$JOB" --project $P --region $R --args "^$D^$ARGS" --async --format='value(metadata.name)')
echo "admin.sh: $EXEC" >&2
for _ in $(seq 1 60); do
  DONE=$(gcloud run jobs executions describe "$EXEC" --project $P --region $R --format='value(status.completionTime)')
  [ -n "$DONE" ] && break
  sleep 3
done
FAILED=$(gcloud run jobs executions describe "$EXEC" --project $P --region $R --format='value(status.failedCount)')

# Logs reach Cloud Logging a few seconds after the task ends.
FILTER="resource.type=\"cloud_run_job\" AND resource.labels.job_name=\"$JOB\" AND labels.\"run.googleapis.com/execution_name\"=\"$EXEC\" AND (logName:\"stdout\" OR logName:\"stderr\")"
for _ in $(seq 1 20); do
  OUT=$(gcloud logging read "$FILTER" --project $P --order=asc --format='value(textPayload)' --freshness=1h)
  [ -n "$OUT" ] && break
  sleep 3
done
echo "$OUT"

gcloud run jobs executions delete "$EXEC" --project $P --region $R --quiet > /dev/null 2>&1 || true
if [ -z "$DONE" ]; then echo "admin.sh: $EXEC did not finish within 3 minutes" >&2; exit 1; fi
if [ -n "$FAILED" ] && [ "$FAILED" != "0" ]; then exit 1; fi
