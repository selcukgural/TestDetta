# Running TestDetta in CI

TestDetta works on any CI that runs .NET and git. GitHub Actions has a ready-made action (`action.yml`,
see the README). Everywhere else, two commands and one folder do the same:

- **On the default branch,** `td record --maps-dir <maps>` runs the whole suite once with
  coverage, and saves one coverage map per test project under `<maps>/<commit>/`.
- **On a pull or merge request,** `td test --base origin/<target> --maps-dir <maps>` restores the
  newest map at or before the merge base. It takes the nearest one within 50 first-parent commits
  (`--max-map-age`), and bridges the changes in between. Then it runs only the affected tests.

`<maps>` is an ordinary folder. Keep it wherever your CI can keep files between pipelines:
- a disk shared by the agents (common on-premises: `/srv/testdetta-maps/<repository>`);
- object storage (S3, Azure Blob, GCS, MinIO) synced before and after.

TestDetta itself never uses the network for maps.

## What every setup needs

- **Full git history.** TestDetta compares with the merge base and walks history to find maps. Shallow
  clones cannot do either.
- **The target branch fetched**, so `origin/<target>` exists. Fetch it with an explicit refspec
  (`+refs/heads/<target>:refs/remotes/origin/<target>`): CI clones often limit the remote's refspecs to
  the pipeline's own branch (GitLab's and Bitbucket's do), and then `git fetch origin <target>`
  alone updates only `FETCH_HEAD` and `origin/<target>` stays missing.
- **The license** in `TESTDETTA_LICENSE` (a masked or secret variable) or a file named by
  `TESTDETTA_LICENSE_FILE`.
  - Public repositories need none on GitLab CI (`CI_PROJECT_VISIBILITY`) and GitHub Actions. Jenkins,
    Azure DevOps and Bitbucket do not tell TestDetta, so there every repository needs one.
  - Without a valid license every test runs, and the build never fails because of it.
  - On a network that cannot reach the license service, set `TESTDETTA_LICENSE_REFRESH=off` and renew the
    license string yourself (docs/design/licensing.md).
- **Exit codes:**
  - `0` success;
  - `1` the analysis failed (`td audit`: it could not run or was cancelled);
  - `2` invalid command line;
  - `3` tests failed (`td test`, `td record`), or a miss (`td audit`).
- **Clean-up:** maps of old commits are only needed for 50 commits. Delete older folders with a
  scheduled job or a storage lifecycle rule.

Install TestDetta as a .NET tool (`dotnet tool install --global <package>`; the package name is set at
launch). Until then, build it from source with `dotnet build src/TestDetta.Cli -c Release -o testdetta-bin`
and call `dotnet testdetta-bin/TestDetta.Cli.dll` wherever `td` appears below.

## GitLab CI

```yaml
variables:
  GIT_DEPTH: "0"                    # full history
  TESTDETTA_MAPS: /srv/testdetta-maps/$CI_PROJECT_PATH   # a disk the runners share; or sync with object storage

record-coverage:
  stage: test
  rules:
    - if: $CI_COMMIT_BRANCH == $CI_DEFAULT_BRANCH
  script:
    - td record --maps-dir "$TESTDETTA_MAPS"

affected-tests:
  stage: test
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
  script:
    - git fetch --no-tags origin "+refs/heads/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME:refs/remotes/origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME"
    - td test --base "origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME" --maps-dir "$TESTDETTA_MAPS" --summary-file testdetta-summary.md
  artifacts:
    when: always
    paths: [testdetta-summary.md]
```

Set `TESTDETTA_LICENSE` under Settings → CI/CD → Variables, masked and protected as needed. TestDetta detects
public projects through `CI_PROJECT_VISIBILITY`.

## Jenkins (declarative pipeline, multibranch)

```groovy
pipeline {
  agent any
  environment {
    TESTDETTA_LICENSE = credentials('testdetta-license')          // a "secret text" credential
    TESTDETTA_MAPS = "/srv/testdetta-maps/${env.JOB_NAME.split('/')[0]}"
  }
  options { skipDefaultCheckout() }
  stages {
    stage('Checkout') {
      steps {
        checkout scmGit(branches: scm.branches, userRemoteConfigs: scm.userRemoteConfigs,
                        extensions: [cloneOption(shallow: false, depth: 0, noTags: true, reference: '')])
      }
    }
    stage('Record coverage') {
      when { branch 'main' }
      steps { sh 'td record --maps-dir "$TESTDETTA_MAPS"' }
    }
    stage('Affected tests') {
      when { changeRequest() }
      steps {
        sh 'git fetch --no-tags origin "+refs/heads/$CHANGE_TARGET:refs/remotes/origin/$CHANGE_TARGET"'
        sh 'td test --base "origin/$CHANGE_TARGET" --maps-dir "$TESTDETTA_MAPS"'
      }
    }
  }
}
```

Jenkins often clones with credentials in the remote URL. TestDetta strips them before matching the license
scope and never logs them. If the remote does not name the repository the license covers (a local
mirror, for example), set `TESTDETTA_LICENSE_SCOPE`, such as `git.acme.corp/scm/pay/app`.

## Azure DevOps Pipelines

```yaml
trigger: [main]
pr: [main]

steps:
  - checkout: self
    fetchDepth: 0
  - script: td record --maps-dir "$(TESTDETTA_MAPS)"
    condition: and(succeeded(), eq(variables['Build.SourceBranch'], 'refs/heads/main'))
    env:
      TESTDETTA_LICENSE: $(TestDettaLicense)          # a secret pipeline variable
  - script: |
      target="${SYSTEM_PULLREQUEST_TARGETBRANCH#refs/heads/}"
      git fetch --no-tags origin "+refs/heads/$target:refs/remotes/origin/$target"
      td test --base "origin/$target" --maps-dir "$(TESTDETTA_MAPS)" --summary-file "$(Agent.TempDirectory)/testdetta.md"
      echo "##vso[task.uploadsummary]$(Agent.TempDirectory)/testdetta.md"
    condition: and(succeeded(), eq(variables['Build.Reason'], 'PullRequest'))
    env:
      TESTDETTA_LICENSE: $(TestDettaLicense)
```

`TESTDETTA_MAPS` points to a share on self-hosted agents. On Microsoft-hosted agents, sync it with Azure
Blob storage (see below).

## Bitbucket Pipelines

```yaml
clone:
  depth: full

pipelines:
  branches:
    main:
      - step:
          script:
            - td record --maps-dir maps
            - ./sync-maps.sh upload maps        # see "Object storage"
  pull-requests:
    '**':
      - step:
          script:
            - git fetch --no-tags origin "+refs/heads/$BITBUCKET_PR_DESTINATION_BRANCH:refs/remotes/origin/$BITBUCKET_PR_DESTINATION_BRANCH"
            - ./sync-maps.sh download maps "origin/$BITBUCKET_PR_DESTINATION_BRANCH"
            - td test --base "origin/$BITBUCKET_PR_DESTINATION_BRANCH" --maps-dir maps
```

Set `TESTDETTA_LICENSE` as a secured repository variable.

## Object storage instead of a shared disk

On hosted runners there is no shared disk. Upload the new folder after recording. Before testing,
download only the folders that can be used: the merge base and up to 50 first-parent commits before
it. Download them newest first, and stop at the first one that exists. An example for S3 (any tool
with `cp` and `ls` works the same way):

```bash
#!/usr/bin/env bash
# sync-maps.sh upload <maps>          | after td record on the default branch
# sync-maps.sh download <maps> <base> | before td test on a pull request
set -euo pipefail
remote="s3://my-bucket/testdetta-maps/my-repository"
case "$1" in
  upload)
    aws s3 cp --recursive "$2/$(git rev-parse HEAD)" "$remote/$(git rev-parse HEAD)" ;;
  download)
    for sha in $(git rev-list --first-parent -n 51 "$(git merge-base "$3" HEAD)"); do
      if aws s3 ls "$remote/$sha/" > /dev/null 2>&1; then
        aws s3 cp --recursive "$remote/$sha" "$2/$sha"
        break
      fi
    done ;;
esac
```

## When there is no map

The first pull requests after setting up, or ones far behind, find no map within reach. TestDetta then
selects by name only, which is safe but selects more, and says so in its log. Selection becomes
precise once `td record` has run on the default branch.
