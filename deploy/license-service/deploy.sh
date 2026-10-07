#!/bin/bash
# Builds the license service image with Cloud Build and deploys it to Cloud Run (docs/design/licensing.md,
# "Hosting"). Run from the repository root after setup.sh: deploy.sh [production|sandbox] (default production).
#   production  license-service at https://license.testdetta.com/, signing key td-2026-10 (the one launch trusts)
#   sandbox     license-service-sandbox for Polar's sandbox, with its own database, service account and signing
#               key, which no TestDetta release ever trusts: a test purchase must never become a real license.
# Reads no secret: Cloud Run mounts them as files.
#   - the connection string, and the Polar webhook secret and Resend key once they exist, under
#     TESTDETTA_SECRETS (one secret per folder, as Cloud Run requires);
#   - the signing key in its own folder, outside TESTDETTA_SECRETS.
# Client addresses: Cloud Run's front end appends the client to X-Forwarded-For and is the only way in,
# so one hop is believed from anywhere (TrustedProxies=*, ForwardedHops=1).
# Public endpoint (owner's decision, 2026-10-07): the organization allows IAM members of its own domain only
# (iam.allowedPolicyMemberDomains), so instead of granting allUsers the invoker role, the service turns Cloud
# Run's invoker check off. The service itself answers unproven requests with 404 and rate-limits them.
set -euo pipefail
P=testdetta-510908 R=europe-west1
BUILDER=license-builder@$P.iam.gserviceaccount.com
SQL=$P:$R:testdetta-licenses

case "${1:-production}" in
  production)
    SERVICE=license-service SA=license-service@$P.iam.gserviceaccount.com
    PREFIX=license KEY_ID=td-2026-10 DOMAIN=license.testdetta.com
    # Trials open at launch, with the trusted key (TRIALS=true deploy.sh production then).
    TRIALS=${TRIALS:-false} TRIAL_ORIGINS=https://testdetta.com ;;
  sandbox)
    SERVICE=license-service-sandbox SA=license-sandbox@$P.iam.gserviceaccount.com
    PREFIX=license-sandbox KEY_ID=td-sandbox-1 DOMAIN=
    # The sandbox takes trials from the live site and from a local build of it (node build.mjs --serve).
    TRIALS=true TRIAL_ORIGINS=https://testdetta.com,http://localhost:4321 ;;
  *) echo "usage: deploy.sh [production|sandbox]" >&2; exit 2 ;;
esac

if [ -n "$(git status --porcelain -- src/TestDetta.Core src/TestDetta.LicenseService global.json Directory.Build.props Directory.Packages.props)" ]; then
  echo "deploy.sh: the service's sources have uncommitted changes; commit them first so the image tag names them" >&2
  exit 1
fi
IMAGE=$R-docker.pkg.dev/$P/testdetta/license-service:$(git rev-parse --short=12 HEAD)

# Secrets: the connection string and the signing key are required; the Polar webhook secret and the Resend key
# are mounted once they exist (without them the webhook answers 404 and no mail is sent).
exists() { gcloud secrets describe "$1" --project $P > /dev/null 2>&1; }
SECRETS="/secrets/db/Licenses__ConnectionString=$PREFIX-connection-string:latest,/keys/signing.pem=$PREFIX-signing-key-$KEY_ID:1"
# Environment variables joined by "|", since MailFrom may hold a comma (gcloud's ^|^ separator, below).
ENV="TESTDETTA_SECRETS=/secrets|Licenses__KeyId=$KEY_ID|Licenses__KeyFile=/keys/signing.pem|Licenses__TrustedProxies=*|Licenses__ForwardedHops=1"
if exists "$PREFIX-polar-webhook-secret"; then
  SECRETS="$SECRETS,/secrets/polar/Licenses__PolarWebhookSecret=$PREFIX-polar-webhook-secret:latest"
fi
if exists "$PREFIX-resend-api-key"; then
  # Replies go to the support inbox: the mail tells buyers to reply when something does not work.
  MAIL_FROM=${MAIL_FROM:-TestDetta <licenses@testdetta.com>} MAIL_REPLY_TO=${MAIL_REPLY_TO:-support@testdetta.com}
  SECRETS="$SECRETS,/secrets/resend/Licenses__ResendApiKey=$PREFIX-resend-api-key:latest"
  ENV="$ENV|Licenses__MailFrom=$MAIL_FROM${MAIL_REPLY_TO:+|Licenses__MailReplyTo=$MAIL_REPLY_TO}"
  # Trials need e-mail: the license goes to the address, which is how the address is checked.
  ENV="$ENV|Licenses__TrialsEnabled=$TRIALS|Licenses__TrialOrigins=$TRIAL_ORIGINS"
fi

gcloud builds submit . --project $P --region $R --config deploy/license-service/cloudbuild.yaml --substitutions "_IMAGE=$IMAGE" \
  --service-account "projects/$P/serviceAccounts/$BUILDER"

# The public base URL goes into every license's refresh link: the domain for production, the service's own
# run.app address for the sandbox (known once the service exists, so the first sandbox deploy sets it after).
if [ -n "$DOMAIN" ]; then
  BASE=https://$DOMAIN/
else
  BASE=$(gcloud run services describe $SERVICE --project $P --region $R --format='value(status.url)' 2>/dev/null || true)
  BASE=${BASE:+$BASE/}
fi
gcloud run deploy $SERVICE --project $P --region $R --image "$IMAGE" \
  --service-account "$SA" --add-cloudsql-instances "$SQL" \
  --set-secrets "$SECRETS" \
  --set-env-vars "^|^$ENV|Licenses__PublicBaseUrl=${BASE:-https://example.invalid/}" \
  --no-invoker-iam-check --ingress all --min-instances 0 --max-instances 2 \
  --cpu 1 --memory 512Mi --concurrency 80 --timeout 30s --execution-environment gen2
if [ -z "$BASE" ]; then
  BASE=$(gcloud run services describe $SERVICE --project $P --region $R --format='value(status.url)')
  [ -n "$BASE" ] || { echo "deploy.sh: $SERVICE has no URL yet; run deploy.sh again" >&2; exit 1; }
  BASE=$BASE/
  gcloud run services update $SERVICE --project $P --region $R --update-env-vars "Licenses__PublicBaseUrl=$BASE"
fi
echo "$SERVICE: $BASE"

# The daily trial job (reminders, then forgetting addresses 7 days after a trial ends): the same image and
# settings, run as `trials daily` by Cloud Scheduler (setup.sh). The service scales to zero and has no clock.
gcloud run jobs deploy $SERVICE-trials --project $P --region $R --image "$IMAGE" \
  --service-account "$SA" --set-cloudsql-instances "$SQL" \
  --set-secrets "$SECRETS" \
  --set-env-vars "^|^$ENV|Licenses__PublicBaseUrl=$BASE" \
  --args trials,daily --tasks 1 --max-retries 1 --task-timeout 10m --cpu 1 --memory 512Mi

# Production's domain: once, after the first deploy. Porkbun then needs the CNAME record this prints
# (license -> ghs.googlehosted.com); Google issues the certificate when the record resolves.
if [ -n "$DOMAIN" ]; then
  if ! gcloud beta run domain-mappings describe --domain $DOMAIN --project $P --region $R > /dev/null 2>&1; then
    gcloud beta run domain-mappings create --service $SERVICE --domain $DOMAIN --project $P --region $R
  fi
  gcloud beta run domain-mappings describe --domain $DOMAIN --project $P --region $R --format='yaml(status.resourceRecords)'
fi
