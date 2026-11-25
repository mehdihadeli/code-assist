#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$script_dir/.."

version="$(dotnet nbgv get-version -v SemVer2)"
version="${version%%+*}"
assembly_version="$(dotnet nbgv get-version -v AssemblyVersion)"
file_version="$(dotnet nbgv get-version -v AssemblyFileVersion)"
informational_version="$(dotnet nbgv get-version -v AssemblyInformationalVersion)"
environment="none"
release_tag="v${version}"

if [[ "${GITHUB_REF:-}" == refs/tags/* ]]; then
  release_tag="${GITHUB_REF#refs/tags/}"
  if [[ ! "$release_tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-rc\.[0-9]+)?$ || "$release_tag" != "v$version" ]]; then
    echo "Release tag $release_tag must be a stable/RC tag matching NBGV version v$version." >&2
    exit 1
  fi
  if [[ "$version" == *-* ]]; then
    environment="staging"
  else
    environment="production"
  fi
elif [[ "${GITHUB_REF:-}" == refs/heads/main && "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-preview\.[1-9][0-9]*$ ]]; then
  environment="dev"
fi

{
  echo "version=${version}"
  echo "assembly_version=${assembly_version}"
  echo "file_version=${file_version}"
  echo "informational_version=${informational_version}"
  echo "environment=${environment}"
  echo "release_tag=${release_tag}"
} | tee -a "${GITHUB_OUTPUT:-/dev/null}"