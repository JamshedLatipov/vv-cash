#!/usr/bin/env pwsh
# Кладёт эталон чека в bozor, где по нему проверяется превью.
#
# Канонический экземпляр — здесь. Обратного направления нет намеренно: раскладку
# определяет боевой рендерер на C#, а не превью.
#
# Правите рендерер — перегенерируйте эталон (VVCASH_UPDATE_GOLDEN=1), поднимите
# RendererVersion в ReceiptPreviewGoldenTest, прогоните этот скрипт и закоммитьте
# результат в bozor. Забудете поднять версию — раскладка разойдётся, а тесты по
# обе стороны останутся зелёными: bozor обязан хранить у себя ОЖИДАЕМОЕ число и
# сравнивать его с "rendererVersion" из этого файла при каждом чтении фикстуры.
# Без такого сравнения там номер версии — комментарий, а не защита. Этот скрипт
# печатает текущее значение ниже именно затем, чтобы это было на виду у того,
# кто копирует.
param([string]$BozorPath = "C:/work/bozor")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$source = Join-Path $root "tests/VvCash.Tests/Fixtures/receipt-golden.json"
$targetDir = Join-Path $BozorPath "src/app/dialogs/cash/receipt-template/__fixtures__"
$target = Join-Path $targetDir "receipt-golden.json"

if (-not (Test-Path $source)) { throw "Эталона нет: $source" }

# Опечатка в -BozorPath или переехавший репозиторий выглядят точно так же, как
# верный путь, если ничего не проверять: New-Item -Force ниже раньше создавал
# ВСЁ дерево от несуществующего корня и бодро сообщал "скопировано" — оператор
# был уверен, что синхронизировал, а файл на самом деле лёг в каталог-фантом
# рядом с настоящим bozor. package.json в корне — дешёвая проверка "это вообще
# похоже на bozor", а не создание каталога вслепую.
if (-not (Test-Path (Join-Path $BozorPath "package.json"))) {
    throw "Не похоже на bozor: в $BozorPath нет package.json. Проверьте -BozorPath."
}

# Целевой каталог создаём только если сама структура bozor уже подтверждена
# (см. проверку выше). Отсутствие каталога превью — повод остановиться и
# поправить путь руками (компонент мог переехать), а не молча завести новый
# каталог рядом со старым.
if (-not (Test-Path $targetDir)) {
    throw "Каталога превью нет: $targetDir. Компонент переехал или путь устарел — поправьте скрипт."
}

# -Encoding utf8 явно на обоих чтениях: файл пишет System.Text.Json как UTF-8
# БЕЗ BOM, а без BOM Windows PowerShell 5.1 угадывает кодировку по системной
# ANSI-кодовой странице, а не по содержимому. На этой машине это сегодня
# работает случайно — все байты кириллицы совпадают с печатными символами
# однобайтовой кодировки, поэтому ConvertFrom-Json не рвётся, а сравнение
# $before/$after корректно, потому что обе стороны портятся ОДИНАКОВО. Ничего
# из этого не гарантировано на другой машине или другой локали, а
# $ErrorActionPreference='Stop' останавливает скрипт уже ПОСЛЕ Copy-Item —
# сломавшийся разбор JSON будет означать, что файл в bozor уже переписан, а
# скрипт упал, сообщив об этом самым непрямым образом.
$before = if (Test-Path $target) { Get-Content $target -Raw -Encoding utf8 } else { $null }
Copy-Item $source $target -Force
$after = Get-Content $target -Raw -Encoding utf8
$version = (ConvertFrom-Json $after).rendererVersion

if ($before -eq $after) {
    Write-Host "Эталон скопирован, но НЕ ИЗМЕНИЛСЯ (rendererVersion=$version). Если вы правили раскладку и ждали изменений — вы забыли перегенерировать эталон (VVCASH_UPDATE_GOLDEN=1) перед запуском этого скрипта."
} else {
    Write-Host "Эталон обновлён: $target (rendererVersion=$version). Пропишите это число как ОЖИДАЕМОЕ в тесте bozor, если оно изменилось, — сравнение с ним и есть единственное, что делает поле rendererVersion защитой, а не комментарием."
}
