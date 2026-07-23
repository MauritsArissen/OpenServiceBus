# Demo load generator. Build context is the repo root:
#   docker build -f src/OpenServiceBus.DemoSeeder/Dockerfile -t openservicebus-demo-seeder .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/OpenServiceBus.DemoSeeder/OpenServiceBus.DemoSeeder.csproj src/OpenServiceBus.DemoSeeder/
RUN dotnet restore src/OpenServiceBus.DemoSeeder/OpenServiceBus.DemoSeeder.csproj

COPY src/OpenServiceBus.DemoSeeder/ src/OpenServiceBus.DemoSeeder/
RUN dotnet publish src/OpenServiceBus.DemoSeeder/OpenServiceBus.DemoSeeder.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS runtime
WORKDIR /app
RUN adduser -D -u 1000 seeder
USER seeder
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "OpenServiceBus.DemoSeeder.dll"]
