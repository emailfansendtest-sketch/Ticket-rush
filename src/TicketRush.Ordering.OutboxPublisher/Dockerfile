FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/TicketRush.Ordering.OutboxPublisher/TicketRush.Ordering.OutboxPublisher.csproj -c Release -o /app/publish
FROM mcr.microsoft.com/dotnet/runtime:10.0
USER $APP_UID
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TicketRush.Ordering.OutboxPublisher.dll"]
