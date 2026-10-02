# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first so the restore layer is cached until a dependency changes.
COPY global.json Stockline.slnx ./
COPY src/Stockline.Domain/*.csproj src/Stockline.Domain/
COPY src/Stockline.Application/*.csproj src/Stockline.Application/
COPY src/Stockline.Infrastructure/*.csproj src/Stockline.Infrastructure/
COPY src/Stockline.Api/*.csproj src/Stockline.Api/
RUN dotnet restore src/Stockline.Api/Stockline.Api.csproj
COPY src ./src
RUN dotnet publish src/Stockline.Api/Stockline.Api.csproj -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Directory sign-in uses the native OpenLDAP client, which the aspnet image does not include.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libldap2 \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /out ./
# The aspnet image ships a non-root `app` user.
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s --start-period=40s \
  CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET /health HTTP/1.0\r\n\r\n" >&3 && head -1 <&3 | grep -q 200'
ENTRYPOINT ["dotnet", "Stockline.Api.dll"]
