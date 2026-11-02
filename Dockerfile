FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /source
COPY global.json Directory.Build.props GroupBuy.sln ./
COPY src/ src/
RUN dotnet restore src/GroupBuy.API/GroupBuy.API.csproj
RUN dotnet publish src/GroupBuy.API/GroupBuy.API.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0
USER 1654:1654
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 CMD ["dotnet", "GroupBuy.API.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "GroupBuy.API.dll"]
