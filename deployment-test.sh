#!/usr/bin/env bash
# Runs the Kubernetes deployment tests. See deployment-test.ps1 for the full notes.
#
# These tests are opt-in: a plain `dotnet test` discovers them and skips them, so CI stays green
# without a cluster. This script sets the switches that turn them on.
#
#   --chart-only     render and assert the Helm chart (needs the aspire CLI and helm, no cluster)
#   --cluster-only   only the tests that talk to a deployed release
#
# The cluster tier writes to its target, so it refuses to run unless the current kubeconfig context
# matches DeploymentTests__ExpectedKubeContext (default: rancher-desktop). Deploy first - see
# docs/K8S-DEPLOYMENT-TESTING-COMMANDS.md.
set -euo pipefail

export RUN_CHART_TESTS=1
export RUN_DEPLOYMENT_TESTS=1

case "${1:-}" in
    --chart-only) unset RUN_DEPLOYMENT_TESTS ;;
    --cluster-only) unset RUN_CHART_TESTS ;;
    "") ;;
    *)
        echo "Unknown option: $1. Expected --chart-only or --cluster-only." >&2
        exit 1
        ;;
esac

dotnet test --project tests/DotNetDistributedApp.DeploymentTests
