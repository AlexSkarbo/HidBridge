FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Platform/ Platform/
RUN dotnet restore Platform/Clients/HidBridge.ControlPlane.Web/HidBridge.ControlPlane.Web.csproj
RUN dotnet publish Platform/Clients/HidBridge.ControlPlane.Web/HidBridge.ControlPlane.Web.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:18110
EXPOSE 18110

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "HidBridge.ControlPlane.Web.dll"]
