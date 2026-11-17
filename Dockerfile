FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first, in its own layer, so editing source does not invalidate the package cache.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Carteiro/Carteiro.csproj src/Carteiro/
RUN dotnet restore src/Carteiro/Carteiro.csproj

COPY src/ src/
RUN dotnet publish src/Carteiro/Carteiro.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

# The aspnet image ships a non-root user; there is no reason to run as root.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Carteiro.dll"]
