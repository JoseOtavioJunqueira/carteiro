# Build context: the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY samples/Receiver/Receiver.csproj samples/Receiver/
RUN dotnet restore samples/Receiver/Receiver.csproj

COPY samples/Receiver/ samples/Receiver/
RUN dotnet publish samples/Receiver/Receiver.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Receiver.dll"]
