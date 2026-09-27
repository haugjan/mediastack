# Zwei Stufen: bauen mit dem SDK, ausliefern mit der schlanken Laufzeit.
# ffmpeg ist dabei, weil die Sender ihre Untertitel als TTML ausliefern und
# Plex damit nichts anfangen kann; die Umwandlung nach SRT macht ffmpeg. Es
# setzt ausserdem HLS-Wiedergabelisten (SRF) zu einer MP4 zusammen. wget
# braucht der Healthcheck in compose.yaml; das schlanke aspnet-Bild bringt es
# nicht mit, und ohne bleibt der Container ewig "unhealthy". xz entpackt die
# Filmliste von MediathekView, die Grundlage fuers Stoebern.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Mediathekarr/Mediathekarr.csproj src/Mediathekarr/
RUN dotnet restore src/Mediathekarr/Mediathekarr.csproj
COPY src/ src/
RUN dotnet publish src/Mediathekarr/Mediathekarr.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends ffmpeg wget xz-utils \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8098
ENTRYPOINT ["dotnet", "Mediathekarr.dll"]
