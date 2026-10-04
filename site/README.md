# Product site

Static site, no dependencies: `node build.mjs` writes `dist/`, `node build.mjs --serve` also serves it
on http://localhost:4321. Pages are HTML fragments in `src/` wrapped in `partials/`; the product
name, prices, contact and legal details come from `site.json` (`{{key}}` in any page).

## Languages

English (`src/`) is the source. Turkish, German, Japanese and Spanish live in `i18n/<lang>/` with
the same paths, and the interface strings (menus, footer, docs navigation) in
`i18n/strings/<lang>.json`. English is served at `/`, the others at `/tr/`, `/de/`, `/ja/`, `/es/`.

**Every change to a page updates all five languages in the same commit.** Each translation
records the English page it was made from (`source: <hash>` in its front matter; `node build.mjs
--hashes` prints the current ones). The build fails when a translation is missing or its English
page changed since, and lists them; `--allow-stale` builds anyway with English in their place, for
local previews only. Code, command output, option names and placeholders are never translated.

## Hosting

Firebase Hosting. `firebase.json` holds the hosting settings, the security headers among them (the
CSP allows no inline styles or scripts). `.github/workflows/site.yml` builds every change and
publishes main with `deploy.mjs`, which sends the same settings through the Hosting REST API, so CI
needs no firebase-tools. By hand: `FIREBASE_SITE=<site id> GOOGLE_ACCESS_TOKEN=$(gcloud auth
print-access-token) node deploy.mjs` after `node build.mjs`.

One-time setup (`PROJECT` is the Google Cloud project, `testdetta-prod` under the `testdetta.com`
organization, `NUMBER` its project number; run gcloud
under the company account, e.g. a separate `gcloud config configurations create testdetta`):

1. Add Firebase to the project and open Hosting once in the Firebase console; its default site id
   is the project id.
2. Enable `iamcredentials.googleapis.com`, `sts.googleapis.com` and `firebasehosting.googleapis.com`.
3. A service account that can only publish:
   ```sh
   gcloud iam service-accounts create site-deployer
   gcloud projects add-iam-policy-binding PROJECT --role=roles/firebasehosting.admin \
     --member=serviceAccount:site-deployer@PROJECT.iam.gserviceaccount.com
   ```
4. (Project owners lack `iam.workloadIdentityPools.create`; grant yourself
   `roles/iam.workloadIdentityPoolAdmin` on the project first.) A workload identity provider that trusts this repository's main branch, this workflow and the
   `site` environment only (the repository is matched by id, so a renamed or recreated repository
   gains nothing):
   ```sh
   gcloud iam workload-identity-pools create github --location=global
   gcloud iam workload-identity-pools providers create-oidc site-deploy --location=global \
     --workload-identity-pool=github --issuer-uri=https://token.actions.githubusercontent.com \
     --attribute-mapping=google.subject=assertion.sub,attribute.repository_id=assertion.repository_id \
     --attribute-condition="assertion.repository_id=='1395823993' && assertion.ref=='refs/heads/main' && assertion.workflow_ref=='selcukgural/TestDetta/.github/workflows/site.yml@refs/heads/main' && assertion.environment=='site'"
   gcloud iam service-accounts add-iam-policy-binding site-deployer@PROJECT.iam.gserviceaccount.com \
     --role=roles/iam.workloadIdentityUser \
     --member=principalSet://iam.googleapis.com/projects/NUMBER/locations/global/workloadIdentityPools/github/attribute.repository_id/1395823993
   ```
5. In GitHub, an environment `site` limited to the main branch, and the repository variables
   `GCP_WORKLOAD_IDENTITY_PROVIDER`
   (`projects/NUMBER/locations/global/workloadIdentityPools/github/providers/site-deploy`),
   `SITE_DEPLOYER_SERVICE_ACCOUNT` and `FIREBASE_SITE`.
6. In Hosting, add the custom domain `testdetta.com` and put the records it shows into the DNS
   (Porkbun), after deleting Porkbun's parking records; add `www.testdetta.com` as a redirect to it.

The custom 404 page is the English one: Hosting serves only the root `404.html`.
