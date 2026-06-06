#!/usr/bin/env pwsh
# Local test runner for VvCash.
# Builds to build/verify-tests so a running app instance can't lock the output
# (see build-lock-workaround). build/ is gitignored.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
dotnet test "$root/tests/VvCash.Tests/VvCash.Tests.csproj" -c Debug -o "$root/build/verify-tests" @args
