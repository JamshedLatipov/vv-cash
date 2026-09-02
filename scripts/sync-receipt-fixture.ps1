#!/usr/bin/env pwsh
# Кладёт эталон чека в bozor, где по нему проверяется превью.
#
# Канонический экземпляр — здесь. Обратного направления нет намеренно: раскладку
# определяет боевой рендерер на C#, а не превью.
#
# Правите рендерер — перегенерируйте эталон (VVCASH_UPDATE_GOLDEN=1), поднимите
# RendererVersion в ReceiptPreviewGoldenTest, прогоните этот скрипт и закоммитьте
# результат в bozor. Забудете — расхождение превью и бумаги не поймает никто.
param([string]$BozorPath = "C:/work/bozor")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Join-Path $root "tests/VvCash.Tests/Fixtures/receipt-golden.json"
$targetDir = Join-Path $BozorPath "src/app/dialogs/cash/receipt-template/__fixtures__"

if (-not (Test-Path $source)) { throw "Эталона нет: $source" }
New-Item -ItemType Directory -Force $targetDir | Out-Null
Copy-Item $source (Join-Path $targetDir "receipt-golden.json") -Force
Write-Host "Эталон скопирован в $targetDir"
