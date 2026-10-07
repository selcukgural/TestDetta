#!/bin/bash
# Builds the license service image with Cloud Build and deploys it to Cloud Run (docs/design/licensing.md,
# "Hosting"). Run from the repository root after setup.sh. Reads no secret: Cloud Run mounts them as files.
#   - the connection string under TESTDETTA_SECRETS (one secret per folder, as Cloud Run requires);
#   - the signing key in its own folder, outside TESTDETTA_SECRETS.
# Client addresses: Cloud Run's front end appends the client to X-Forwarded-For and is the only way in,
# so one hop is believed from anywhere (TrustedProxies=*, ForwardedHops=1).
set -euo pipefail
P=testdetta-510908 R=europe-west1
SA=license-service@$P.iam.gserviceaccount.com
BUILDER=license-builder@$P.iam.gserviceaccount.com
SQL=$P:$R:testdetta-licenses
KEY_ID=td-2026-10

if [ -n "$(git status --porcelain -- src/TestDetta.Core src/TestDetta.LicenseService global.json Directory.Build.props Directory.Packages.props)" ]; then
  echo "deploy.sh: the service's sources have uncommitted changes; commit them first so the image tag names them" >&2
  exit 1
fi
IMAGE=$R-docker.pkg.dev/$P/testdetta/license-service:$(git rev-parse --short=12 HEAD)

gcloud builds submit . --project $P --region $R --config deploy/license-service/cloudbuild.yaml --substitutions "_IMAGE=$IMAGE" \
  --service-account "projects/$P/serviceAccounts/$BUILDER"
gcloud run deploy license-service --project $P --region $R --image "$IMAGE" \
  --service-account "$SA" --add-cloudsql-instances "$SQL" \
  --set-secrets "/secrets/db/Licenses__ConnectionString=license-connection-string:latest,/keys/signing.pem=license-signing-key-$KEY_ID:1" \
  --set-env-vars "TESTDETTA_SECRETS=/secrets,Licenses__KeyId=$KEY_ID,Licenses__KeyFile=/keys/signing.pem,Licenses__PublicBaseUrl=https://license.testdetta.com/,Licenses__TrustedProxies=*,Licenses__ForwardedHops=1" \
  --allow-unauthenticated --ingress all --min-instances 0 --max-instances 2 \
  --cpu 1 --memory 512Mi --concurrency 80 --timeout 30s --execution-environment gen2

# license.testdetta.com: once, after the first deploy. Porkbun then needs the CNAME record this prints
# (license -> ghs.googlehosted.com); Google issues the certificate when the record resolves.
if ! gcloud beta run domain-mappings describe --domain license.testdetta.com --project $P --region $R > /dev/null 2>&1; then
  gcloud beta run domain-mappings create --service license-service --domain license.testdetta.com --project $P --region $R
fi
gcloud beta run domain-mappings describe --domain license.testdetta.com --project $P --region $R --format='yaml(status.resourceRecords)'
