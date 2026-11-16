# OS Service

Microsserviço responsável pela abertura, consulta e histórico da ordem de serviço.
Usa PostgreSQL privado e não acessa bancos de outros serviços. O endpoint de
abertura inicia a Saga orquestrada e aplica compensação `BillingFailed` quando o
billing-service não aceita o orçamento.

## Executar

```powershell
$env:ConnectionStrings__ServiceOrders = "Host=localhost;Port=5432;Database=os_service;Username=garage;Password=change-me"
dotnet run
```

Contrato HTTP: `POST /service-orders`, `GET /service-orders/{id}` e `GET /health`.
Eventos críticos são persistidos na Outbox PostgreSQL e publicados pelo RabbitMQ
com retry. `QuoteApproved` encaminha a OS para o execution-service e
`ExecutionCompleted` finaliza a OS. O contrato versionado está em
`garage-management-api-dotnet/contracts/events`.
