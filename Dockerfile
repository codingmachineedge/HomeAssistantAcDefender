FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY HomeAssistantAcDefender.csproj ./
RUN dotnet restore

COPY . ./
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV Defender__StateFilePath=/data/defender-state.json

RUN mkdir /data
VOLUME ["/data"]
EXPOSE 8080

COPY --from=build /app/publish ./
ENTRYPOINT ["dotnet", "HomeAssistantAcDefender.dll"]
