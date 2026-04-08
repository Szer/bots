# Platform is chosen at build time: no --platform => host (amd64 on Windows x64);
# deploy uses: docker buildx --platform=linux/arm64 => arm64.
FROM mcr.microsoft.com/dotnet/sdk:10.0.102-noble AS build

### workaround for testcontainers resource reaper issue
ARG RESOURCE_REAPER_SESSION_ID="00000000-0000-0000-0000-000000000000"
LABEL "org.testcontainers.resource-reaper-session"=$RESOURCE_REAPER_SESSION_ID
### end of workaround

WORKDIR /repo
COPY Directory.Build.props Directory.Packages.props global.json NuGet.Config ./
COPY src/BotInfra/BotInfra.fsproj src/BotInfra/
COPY src/CouponHubBot/CouponHubBot.fsproj src/CouponHubBot/
RUN dotnet restore src/CouponHubBot/CouponHubBot.fsproj

COPY src/BotInfra/ src/BotInfra/
COPY src/CouponHubBot/ src/CouponHubBot/
RUN dotnet publish src/CouponHubBot/CouponHubBot.fsproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.2-noble AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish .
ENTRYPOINT ["dotnet", "CouponHubBot.dll"]
