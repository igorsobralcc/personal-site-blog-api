FROM mcr.microsoft.com/dotnet/sdk:10.0.302@sha256:3d1b9b2646210a51b2b70c61b32b413fe0eddd764a7e5192d360007822518bcd AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/BlogApi/BlogApi.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10@sha256:069773c15099b1d87f250508c64ded8fe6d443d2824436ab4f1eb1660270f36c
RUN adduser --disabled-password --gecos "" --uid 10001 blog
WORKDIR /app
COPY --from=build /app .
USER 10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/health/live || exit 1"]
ENTRYPOINT ["dotnet", "BlogApi.dll"]
