#!/bin/bash
# One-time setup of the license service's Google Cloud resources (docs/design/licensing.md, "Hosting").
# Already done by hand on 2026-10-07: the APIs, the Cloud SQL instance testdetta-licenses, the Artifact
# Registry repository testdetta and the service account license-service. The two secrets are made by a
# person, never by a script that could print them:
#   license-signing-key-td-2026-10  openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 | gcloud secrets create ... --data-file=-
#   license-connection-string       Host=/cloudsql/<instance>;Database=licenses;Username=license_service;Password=<random>
# This script reads no secret: it grants access to them and creates the database and the domain mapping.
set -euo pipefail
P=testdetta-510908 R=europe-west1
SA=license-service@$P.iam.gserviceaccount.com

gcloud projects add-iam-policy-binding $P --member "serviceAccount:$SA" --role roles/cloudsql.client --condition=None
for secret in license-connection-string license-signing-key-td-2026-10; do
  gcloud secrets add-iam-policy-binding $secret --project $P --member "serviceAccount:$SA" --role roles/secretmanager.secretAccessor
done
gcloud sql databases create licenses --instance testdetta-licenses --project $P
