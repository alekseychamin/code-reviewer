FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
ARG NUGET_INTERNAL_FEED_URL=
ARG NUGET_INTERNAL_FEED_USERNAME=
ARG NUGET_INTERNAL_FEED_PASSWORD=
ENV NUGET_INTERNAL_FEED_URL=$NUGET_INTERNAL_FEED_URL
ENV NUGET_INTERNAL_FEED_USERNAME=$NUGET_INTERNAL_FEED_USERNAME
ENV NUGET_INTERNAL_FEED_PASSWORD=$NUGET_INTERNAL_FEED_PASSWORD
RUN dotnet restore TfsReviewPlatform.sln --ignore-failed-sources
RUN dotnet publish tools/RoslynGraphBuilder/RoslynGraphBuilder.csproj -c Release -o /app/roslyn --no-restore
RUN dotnet publish src/TfsReviewPlatform.Api/TfsReviewPlatform.Api.csproj -c Release -o /app/publish --no-restore

FROM ghcr.io/hmbown/deepseek-tui:latest AS deepseek_tui

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends git ca-certificates libdbus-1-3 nodejs npm \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
RUN mkdir -p /root/.nuget/NuGet
COPY NuGet.Config /root/.nuget/NuGet/NuGet.Config
COPY --from=build /app/publish .
COPY --from=build /app/roslyn ./roslyn
COPY --from=deepseek_tui /usr/local/bin/deepseek /usr/local/bin/deepseek
COPY --from=deepseek_tui /usr/local/bin/deepseek-tui /usr/local/bin/deepseek-tui
ENV ReviewPipeline__Roslyn__GraphBuilderDllPath=/app/roslyn/RoslynGraphBuilder.dll
EXPOSE 8080
ENTRYPOINT ["dotnet", "TfsReviewPlatform.Api.dll"]
