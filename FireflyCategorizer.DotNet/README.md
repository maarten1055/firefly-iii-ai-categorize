# Firefly III AI Categorizer .NET backend

This project is the ASP.NET Core rewrite of the original JavaScript backend.

It keeps the same main API shape, serves the UI from `wwwroot/`, and uses SignalR for live job updates.

## Required environment variables

- `FIREFLY_URL`: Base URL of your Firefly III instance, for example `https://firefly.example.com`
- `FIREFLY_PERSONAL_TOKEN`: Firefly III personal access token

You also need one AI provider:

- `MISTRAL_API_KEY`: preferred when present
- `OPENAI_API_KEY`: used when Mistral is not configured

## Optional environment variables

- `ENABLE_UI=true`: serves `index.html` and `uncategorized.html`
- `FIREFLY_TAG`: tag added to updated transactions, default `AI categorized`
- `MISTRAL_MODEL`: default `mistral-small-latest`
- `OPENAI_MODEL`: default `gpt-4o-mini`
- `ASPNETCORE_URLS`: bind address, for example `http://127.0.0.1:5087`

## Local development

Create `FireflyCategorizer.DotNet/.env` from `FireflyCategorizer.DotNet/.env.example` and fill in your values.

From the repository root, you can then start the backend with one command:

```powershell
dotnet run --project '.\FireflyCategorizer.DotNet\FireflyCategorizer.DotNet.csproj' --no-launch-profile
```

The app loads `.env` automatically from the project directory. Explicit shell environment variables still win if both are set.

If you prefer not to use `.env`, you can still set variables manually:

```powershell
$env:FIREFLY_URL='https://firefly.example.com'
$env:FIREFLY_PERSONAL_TOKEN='eyabc123...'
$env:MISTRAL_API_KEY='mistral-abc123...'
$env:ENABLE_UI='true'
dotnet run --project '.\FireflyCategorizer.DotNet\FireflyCategorizer.DotNet.csproj'
```

If you want `ASPNETCORE_URLS` to override the port, skip the launch profile:

```powershell
$env:ASPNETCORE_URLS='http://127.0.0.1:5087'
dotnet run --project '.\FireflyCategorizer.DotNet\FireflyCategorizer.DotNet.csproj' --no-launch-profile
```

## Docker

The root `Dockerfile` now builds the .NET backend by default.

Build from the repository root:

```powershell
docker build -t firefly-iii-ai-categorize-dotnet .
```

Run the container:

```powershell
docker run -d `
  -p 3000:8080 `
  -e FIREFLY_URL=https://firefly.example.com `
  -e FIREFLY_PERSONAL_TOKEN=eyabc123... `
  -e ENABLE_UI=true `
  -e MISTRAL_API_KEY=mistral-abc123... `
  --name firefly-iii-ai-categorize-dotnet `
  firefly-iii-ai-categorize-dotnet
```

## Compose

```yaml
services:
  categorizer-dotnet:
    build:
      context: ./FireflyCategorizer.DotNet
      dockerfile: Dockerfile
    restart: unless-stopped
    ports:
      - "3000:8080"
    environment:
      FIREFLY_URL: "https://firefly.example.com"
      FIREFLY_PERSONAL_TOKEN: "eyabc123..."
      ENABLE_UI: "true"
      FIREFLY_TAG: "AI categorized"
      MISTRAL_API_KEY: "mistral-abc123..."
```

## Migration notes from the JavaScript backend

- The .NET app serves the frontend directly from `FireflyCategorizer.DotNet/wwwroot/`.
- Realtime job updates use SignalR at `/hubs/jobs` instead of Socket.IO.
- The dashboard root `/` only serves the UI when `ENABLE_UI=true`.
- If both `MISTRAL_API_KEY` and `OPENAI_API_KEY` are set, Mistral is used first.
- The root `Dockerfile` targets the .NET backend now.
- The original Node files are still in the repository, but they are no longer required for running the .NET backend.
