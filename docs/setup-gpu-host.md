# Настройка машины обработчика

Что нужно поставить на компьютере, где будет работать распознавание и ввод в кабинет.

## Обязательное

| Компонент | Зачем | Проверка |
|---|---|---|
| .NET SDK 8.0.4xx | версия зафиксирована в `global.json` | `dotnet --version` |
| Git | клонирование репозитория | `git --version` |
| NVIDIA GPU, CC ≥ 5.0, от 8 ГБ VRAM | локальная VLM | `nvidia-smi` |
| Ollama | хост модели | `ollama --version` |
| Браузер Playwright | ввод в кабинет | скачивается сам при первом `dotnet test` |

Проверить возможности видеокарты: `nvidia-smi --query-gpu=name,memory.total,compute_cap --format=csv`.
При compute capability ниже 5.0 Ollama работать не будет.

## Первый запуск

```powershell
git clone https://github.com/ytkachov/HomeWaterCounters.git
cd HomeWaterCounters

# App key Dropbox (в репозитории не хранится)
Copy-Item dropbox.local.props.example dropbox.local.props
# подставить ключ внутри файла

dotnet build WaterCounters.sln
dotnet test  WaterCounters.sln
```

Первый `dotnet test` скачивает браузер Playwright (около 300 МБ) — поэтому он идёт
заметно дольше остальных. Отдельная команда для этого не нужна.

Привязка Dropbox — на каждой машине своя, токен не переносится:

```powershell
dotnet run --project tools/WaterCounters.DropboxSetup -- login
dotnet run --project tools/WaterCounters.DropboxSetup -- smoke
```

`smoke` должен пройти целиком: он проверяет конфликты при повторной записи,
атомарность перемещения и реакцию longpoll — свойства, на которых держится очередь.

## Модель распознавания

```powershell
ollama pull qwen2.5vl:7b     # 8 ГБ VRAM
ollama pull qwen2.5vl:32b    # 24 ГБ VRAM
ollama serve
```

Endpoint по умолчанию `http://localhost:11434`. Модель и адрес задаются в настройках,
поэтому VLM-хост можно вынести на отдельную машину в локальной сети.

## Что перенести с машины разработки

Ничего из секретов переносить не нужно и не следует:

* **refresh-токен Dropbox** зашифрован DPAPI и привязан к учётной записи Windows —
  на другой машине файл бесполезен, нужен свой `login`;
* **app key Dropbox** — один и тот же, кладётся в `dropbox.local.props` руками;
* **мастер-пароль** для `secrets.enc` вводится на каждом устройстве отдельно.

## Проверка перед первым боевым месяцем

1. Разложить фотографии в `/photos/<yyyy-MM>/` с именами по ключам счётчиков.
2. Убедиться, что `portal.dryRun = true`.
3. Дождаться письма: сверить распознанные значения и скриншот заполненной формы.
4. Только после этого переключать `dryRun` в `false`.
