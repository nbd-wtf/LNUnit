FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

ARG GITLAB_USER
ARG GITLAB_TOKEN
RUN echo '<?xml version="1.0" encoding="utf-8"?>' > NuGet.config && \
              echo '<configuration>'  >> NuGet.config  %% \
              echo '<packageSources>'  >> NuGet.config  %% \
              echo '<add key="NuGet" value="https://www.nuget.org/api/v2/" />'  >> NuGet.config  %% \
              echo '<add key="GitLab" value="https://git.zebedee.team/api/v4/groups/144/-/packages/nuget/index.json" protocolVersion="3" />'  >> NuGet.config  %% \
              echo '</packageSources>'  >> NuGet.config  %% \
              echo '<packageSourceCredentials>'  >> NuGet.config  %% \
              echo '<GitLab>'  >> NuGet.config  %% \
              echo '  <add key="Username" value="gitlabpipeline" />'  >> NuGet.config  %% \
              echo "  <add key='ClearTextPassword' value='${GITLAB_TOKEN}' />"  >> NuGet.config  %% \
              echo '</GitLab>'  >> NuGet.config  %% \
              echo '</packageSourceCredentials>'  >> NuGet.config  %% \
              echo '</configuration>'  >> NuGet.config  

COPY ["LNUnit/LNUnit.csproj", "LNUnit/"]
RUN dotnet restore "LNUnit/LNUnit.csproj"
COPY . .
WORKDIR "/src/LNUnit"
RUN dotnet build "LNUnit.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "LNUnit.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
EXPOSE 80
ENV ASPNETCORE_HTTP_PORTS=80
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "LNUnit.dll"]
