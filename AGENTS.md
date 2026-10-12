# AGENTS.md

Инструкции для AI-агентов, работающих с этим репозиторием.

## О проекте

Учебный бэкенд в духе TikTok на .NET 8: три независимых микросервиса
(`Identity`, `Content`, `Engagement`), общающихся по gRPC, каждый — по Clean
Architecture (`Api → Application → Infrastructure → Domain`). Общий код — в
`Shared`. Подробности о сервисах, портах и стеке — в [README.md](README.md).

## Структура решения

```
src/
  Shared/                                        — общие контракты: ошибки, JWT, protobuf, Polly-политики
  {Identity,Content,Engagement}.Domain/           — сущности, доменные исключения, БЕЗ внешних зависимостей
  {Identity,Content,Engagement}.Application/      — use case'ы, DTO, интерфейсы репозиториев/сервисов
  {Identity,Content,Engagement}.Infrastructure/   — EF Core, gRPC-клиенты, внешние сервисы
  {Identity,Content,Engagement}.Api/              — контроллеры, gRPC-эндпоинты, Program.cs
tests/
  Engagement.IntegrationTests/                    — интеграционные тесты через WebApplicationFactory + фейки
mocks/
  vision-mock/, push-mock/                        — HTTP-заглушки внешних сервисов для докера
scripts/
  check_architecture.py                           — статический чекер конвенций (см. ниже)
```

## Сборка, тесты, проверки

```bash
# Собрать всё решение
dotnet build TikTokFeed.sln

# Прогнать интеграционные тесты
dotnet test tests/Engagement.IntegrationTests/Engagement.IntegrationTests.csproj

# Поднять все сервисы + БД + моки локально
docker compose up --build

# Проверить код на нарушение архитектурных конвенций (regex-линтер, без LLM)
python3 scripts/check_architecture.py
```

`TreatWarningsAsErrors=true` для всего решения (см. `Directory.Build.props`) —
любое предупреждение анализаторов (StyleCop, SourceKit) валит сборку. Перед
тем как считать задачу выполненной, прогоняй `dotnet build`, а не только
локальный `dotnet build` конкретного проекта — предупреждение в одном слое
может быть unused-using или nullable-warning, которые здесь фатальны.

## Архитектурные правила (обязательны)

Направление зависимостей внутри каждого контекста строгое:

```
Domain  ←  Application  ←  Infrastructure  ←  Api
```

- **Domain** не ссылается на Application/Infrastructure/Api, не знает про
  `Microsoft.EntityFrameworkCore` или `Microsoft.AspNetCore.*`. Только
  сущности, value objects, доменные исключения.
- **Application** зависит только от Domain.
- **Infrastructure** реализует интерфейсы Application/Domain (репозитории,
  gRPC-gateway'и), зависит от Application + Domain + Shared.
- **Api** не обращается к `DbContext` напрямую — только через
  репозитории/сервисы Application.
- Контексты (`Identity`/`Content`/`Engagement`) не ссылаются друг на друга
  напрямую — только через gRPC-клиенты в Infrastructure или общий код в
  `Shared`.

## Конвенции кода

- Интерфейсы — с префиксом `I` (`IFavouriteRepository`, `IJwtTokenGenerator`).
- Один репозиторий на агрегат; интерфейс — в Application/Domain, реализация —
  в Infrastructure (см. `*.Infrastructure/Persistence/Repositories`).
- Доменные ошибки наследуются от `DomainException` (база для каждого
  контекста): `NotFoundException`, `ConflictException`, `ForbiddenException`,
  `ValidationException` (+ `InvalidCredentialsException` только в Identity).
  Не бросай голый `Exception`.
- Логирование — через `ILogger<T>` из DI. `Console.WriteLine`/`Console.Write`
  запрещены.
- `async void` запрещён — только `async Task`/`async Task<T>`.
- Блокирующее ожидание `.Result`/`.Wait()` на `Task` запрещено — используй
  `await` до конца цепочки (исключение — `.Result` на объектах Polly вроде
  `DelegateResult<T>`, это не `Task`, а часть API самой библиотеки).
- Пустые `catch {}` запрещены: исключение либо обрабатывается, либо
  логируется, либо пробрасывается.
- Секреты в `appsettings.json`, которые коммитятся в репозиторий — только
  dev-заглушки (как сейчас `postgres`/`dev-super-secret-...`). Боевые
  значения — через переменные окружения/секрет-хранилище, никогда в git.
- `TODO`/`FIXME`/`HACK` — временная метка. Закрывай такие места перед мержем
  в `main`, не оставляй агенту следующего запуска.

`scripts/check_architecture.py` проверяет часть этих правил автоматически
(regex по `*.cs`/`*.json`, без запуска кода) — гоняй его после правок слоёв
Domain/Application или контроллеров, чтобы не сломать изоляцию слоёв.

## Git и коммиты

- `main` — актуальная интеграционная ветка, синхронизирована с
  `origin/main`.
- Перед созданием коммита — `git status`/`git diff`, чтобы не закоммитить
  секреты или файлы из `bin/`/`obj/` (они должны быть в `.gitignore`).
- Не пуш­ить в `origin` без явного запроса пользователя.

## Чего избегать

- Не добавляй прямые ссылки на EF Core/ASP.NET Core в Domain-проекты.
- Не инжектируй `DbContext` в контроллеры или gRPC-сервисы.
- Не создавай кросс-контекстные `ProjectReference` в обход gRPC-контрактов.
- Не оставляй закомментированный мёртвый код или debug-принты
  (`Console.WriteLine`) в финальном варианте изменений.
