FROM mcr.microsoft.com/dotnet/sdk:10.0.102 AS build
WORKDIR /src
ARG FORCE_REBUILD=0

COPY Directory.Build.props global.json ./
COPY tests/FakeAzureOcrApi/FakeAzureOcrApi.fsproj tests/FakeAzureOcrApi/
RUN dotnet restore tests/FakeAzureOcrApi/FakeAzureOcrApi.fsproj
COPY tests/FakeAzureOcrApi/ tests/FakeAzureOcrApi/
RUN dotnet publish tests/FakeAzureOcrApi/FakeAzureOcrApi.fsproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0.2 AS final
WORKDIR /app
COPY --from=build /publish .
ENV ASPNETCORE_URLS=http://*:8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "FakeAzureOcrApi.dll"]
