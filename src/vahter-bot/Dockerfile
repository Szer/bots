FROM mcr.microsoft.com/dotnet/sdk:10.0.102-noble AS build

### workaround for testcontainers resource reaper issue
ARG RESOURCE_REAPER_SESSION_ID="00000000-0000-0000-0000-000000000000"
LABEL "org.testcontainers.resource-reaper-session"=$RESOURCE_REAPER_SESSION_ID
### end of workaround

WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json NuGet.Config ./
COPY src/BotInfra/BotInfra.fsproj src/BotInfra/
COPY src/VahterBanBot/VahterBanBot.fsproj src/VahterBanBot/
RUN dotnet restore src/VahterBanBot/VahterBanBot.fsproj

COPY src/BotInfra/ src/BotInfra/
COPY src/VahterBanBot/ src/VahterBanBot/
RUN dotnet publish src/VahterBanBot/VahterBanBot.fsproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.2-noble AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /publish
COPY --from=build /publish .
ENTRYPOINT ["dotnet", "VahterBanBot.dll"]
