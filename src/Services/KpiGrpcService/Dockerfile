FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/Services/KpiGrpcService/KpiGrpcService.csproj", "src/Services/KpiGrpcService/"]
RUN dotnet restore "src/Services/KpiGrpcService/KpiGrpcService.csproj"

COPY ["src/Services/KpiGrpcService/", "src/Services/KpiGrpcService/"]

WORKDIR "/src/src/Services/KpiGrpcService"

RUN test -f "Protos/kpi.proto"
RUN dotnet build "KpiGrpcService.csproj" \
    -c Release \
    -o /app/build \
    --no-restore

FROM build AS publish

RUN dotnet publish "KpiGrpcService.csproj" \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false \
    --no-restore

FROM base AS final
WORKDIR /app

COPY --from=publish /app/publish .

ENTRYPOINT ["dotnet", "KpiGrpcService.dll"]

