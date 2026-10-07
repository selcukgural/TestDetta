#!/bin/bash
# One-time: runs the daily trial job (deploy.sh deploys it as <service>-trials) every morning with Cloud
# Scheduler (docs/design/licensing.md, "Trials"). Usage: schedule-trials.sh [production|sandbox]. Run after the
# first deploy.sh of that environment. Reads no secret.
# The scheduler calls the Run API as its own account, license-scheduler, which may run these jobs and nothing
# else (roles/run.invoker on each job, not on the project).
set -euo pipefail
P=testdetta-510908 R=europe-west1
CALLER=license-scheduler@$P.iam.gserviceaccount.com

case "${1:-production}" in
  production) JOB=license-service-trials ;;
  sandbox) JOB=license-service-sandbox-trials ;;
  *) echo "usage: schedule-trials.sh [production|sandbox]" >&2; exit 2 ;;
esac

gcloud services enable cloudscheduler.googleapis.com --project $P
if ! gcloud iam service-accounts describe "$CALLER" --project $P > /dev/null 2>&1; then
  gcloud iam service-accounts create license-scheduler --project $P --display-name "Runs the license service's scheduled jobs"
fi
gcloud run jobs add-iam-policy-binding $JOB --project $P --region $R --member "serviceAccount:$CALLER" --role roles/run.invoker

# 07:00 Istanbul time: reminders go out in the morning for Europe.
URI="https://run.googleapis.com/v2/projects/$P/locations/$R/jobs/$JOB:run"
if gcloud scheduler jobs describe $JOB-daily --project $P --location $R > /dev/null 2>&1; then
  gcloud scheduler jobs update http $JOB-daily --project $P --location $R --schedule "0 7 * * *" --time-zone "Europe/Istanbul" \
    --uri "$URI" --http-method POST --oauth-service-account-email "$CALLER"
else
  gcloud scheduler jobs create http $JOB-daily --project $P --location $R --schedule "0 7 * * *" --time-zone "Europe/Istanbul" \
    --uri "$URI" --http-method POST --oauth-service-account-email "$CALLER"
fi
