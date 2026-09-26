# Zwei Stufen: bauen mit dem SDK, ausliefern mit der schlanken Laufzeit.
# ffmpeg und ffprobe gehoeren ins Endbild, sie machen Messung und Schnitt.
# wget braucht der Healthcheck in compose.yaml; das schlanke aspnet-Bild
# bringt es nicht mit, und ohne bleibt der Container ewig "unhealthy".
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Aircheckarr/Aircheckarr.csproj src/Aircheckarr/
RUN dotnet restore src/Aircheckarr/Aircheckarr.csproj
COPY src/ src/
RUN dotnet publish src/Aircheckarr/Aircheckarr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg wget \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8099
ENTRYPOINT ["dotnet", "Aircheckarr.dll"]
