# DiagLabCourse

Учебный стенд для курса «Диагностика и профилирование .NET».

`DiagLab` — небольшой веб-сервис на ASP.NET Core (.NET 10): каталог, выгрузки отчётов,
курсы валют, склад, пользовательские сессии. Код написан так, как пишут обычный рабочий
сервис. Курс учит снимать с него метрики, дампы и трассировки инструментами `dotnet-*`
и по ним разбираться, как он ведёт себя под нагрузкой.

`LoadGen` — генератор HTTP-нагрузки для стенда: консольное приложение, одинаково работающее
на Windows, Linux и macOS.

---

## Что нужно

* .NET SDK 10.0 — проверка: `dotnet --version`
* диагностические инструменты курса (установка — в модуле 0):

```bash
dotnet tool install --global dotnet-counters
dotnet tool install --global dotnet-gcdump
dotnet tool install --global dotnet-dump
dotnet tool install --global dotnet-trace
```

---

## Запуск стенда

```bash
git clone https://github.com/Allezard/DiagLabCourse.git
cd DiagLabCourse
dotnet run --project DiagLab
```

Сервис слушает `http://localhost:5199`. Проверка:

```powershell
Invoke-RestMethod http://localhost:5199/api/health
```

```bash
curl http://localhost:5199/api/health
```

Сборка Release — ключом `-c Release`:

```bash
dotnet run --project DiagLab -c Release
```

---

## Генератор нагрузки

Ставится как глобальный инструмент .NET. Из корня репозитория:

```bash
dotnet pack LoadGen -c Release
dotnet tool install --global --add-source LoadGen/nupkg DiagLab.LoadGen
```

```bash
loadgen --list                # сценарии
loadgen mixed -d 120 -c 8     # смешанная нагрузка, 8 клиентов, 2 минуты
loadgen --help                # все параметры
```

Команды одинаковы в PowerShell и bash. Без установки: `dotnet run --project LoadGen -c Release -- mixed -d 60`.

Подробно — в [LoadGen/README.md](LoadGen/README.md).

---

## Docker

Образ собирается из корня репозитория: `Dockerfile` лежит в `DiagLab/`, но контекст сборки — корень.

```bash
docker build -f DiagLab/Dockerfile -t diaglab:1.0 .
```

---

## Структура

```
DiagLabCourse/
├── DiagLab.slnx
├── DiagLab/                  веб-сервис
│   ├── Controllers/          маршруты: принимают запрос и вызывают сервис
│   ├── Services/             логика
│   ├── appsettings.json      настройки, секция DiagLab
│   └── Dockerfile            образ для запуска в контейнере
└── LoadGen/                  генератор нагрузки
    └── loadgen.json          сценарии нагрузки
```

Артефакты диагностики (`*.gcdump`, `*.dmp`, `*.nettrace`, каталог `artifacts/`) в git не попадают —
см. `.gitignore`.
