FROM mcr.microsoft.com/dotnet/sdk:10.0.400@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/BlogApi/BlogApi.csproj -c Release -o /app --no-self-contained
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
RUN adduser --disabled-password --gecos "" --uid 10001 blog
WORKDIR /app
COPY --from=build /app .
USER 10001
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=3s CMD ["/bin/sh", "-c", "wget -qO- http://127.0.0.1:8080/health/live || exit 1"]
ENTRYPOINT ["dotnet", "BlogApi.dll"]
