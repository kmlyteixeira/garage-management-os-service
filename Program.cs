using System.Data;
using System.Text;
using System.Text.Json;
using Npgsql;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
ConfigureObservability(builder);
ConfigureAuthentication(builder);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IServiceOrderStore, PostgresServiceOrderStore>();
builder.Services.AddHttpClient<ISagaParticipantClient, SagaParticipantClient>();
builder.Services.AddSingleton<OpenServiceOrderSaga>();
builder.Services.AddSingleton<IEventBus, RabbitMqEventBus>();
builder.Services.AddHostedService<OutboxPublisher>();
builder.Services.AddHostedService<SagaEventConsumer>();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health").AllowAnonymous();

static void ConfigureObservability(WebApplicationBuilder builder)
{
    var endpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
    if (string.IsNullOrWhiteSpace(endpoint)) return;
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(resource => resource.AddService("garage-management-os-service"))
        .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)))
        .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddRuntimeInstrumentation().AddOtlpExporter(options => options.Endpoint = new Uri(endpoint)));
}

static void ConfigureAuthentication(WebApplicationBuilder builder)
{
    var enabled = builder.Configuration.GetValue<bool>("ExternalJwt:Enabled");
    var authentication = builder.Services.AddAuthentication();
    builder.Services.AddAuthorization(options =>
    {
        if (enabled)
            options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build();
    });
    if (!enabled) return;
    var publicKey = builder.Configuration["ExternalJwt:PublicKeyPem"]
        ?? throw new InvalidOperationException("ExternalJwt:PublicKeyPem não foi configurada.");
    using var rsa = RSA.Create();
    rsa.ImportFromPem(publicKey);
    authentication.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["ExternalJwt:Issuer"],
                ValidateAudience = true,
                ValidAudience = builder.Configuration["ExternalJwt:Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new RsaSecurityKey(rsa.ExportParameters(false)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
}

app.MapPost("/service-orders", async (
    OpenServiceOrderRequest request,
    OpenServiceOrderSaga saga,
    IServiceOrderStore store,
    CancellationToken cancellationToken) =>
{
    if (request.CustomerId == Guid.Empty || request.VehicleId == Guid.Empty)
        return Results.BadRequest(new { error = "customerId e vehicleId são obrigatórios." });
    if (request.ServiceItems?.Any(item => item.Quantity <= 0) == true ||
        request.PartItems?.Any(item => item.Quantity <= 0) == true)
        return Results.BadRequest(new { error = "As quantidades dos itens devem ser maiores que zero." });
    if (request.ServiceItems is { Count: > 0 } && request.Amount < 0)
        return Results.BadRequest(new { error = "amount não pode ser negativo." });
    if (!await store.CustomerExistsAsync(request.CustomerId, cancellationToken) ||
        !await store.VehicleBelongsToCustomerAsync(request.VehicleId, request.CustomerId, cancellationToken))
        return Results.NotFound(new { error = "Cliente ou veículo não encontrado." });

    var result = await saga.StartAsync(request, cancellationToken);
    return Results.Created($"/service-orders/{result.ServiceOrderId}", result);
}).WithOpenApi();

app.MapPost("/customers", async (
    CustomerRequest request,
    IServiceOrderStore store,
    CancellationToken cancellationToken) =>
{
    var customer = new CustomerResponse(Guid.NewGuid(), request.Name, request.Email, request.PhoneNumber, request.Document, true);
    await store.SaveCustomerAsync(customer, cancellationToken);
    return Results.Created($"/customers/{customer.CustomerId}", customer);
}).WithOpenApi();

app.MapPost("/vehicles", async (
    VehicleRequest request,
    IServiceOrderStore store,
    CancellationToken cancellationToken) =>
{
    var vehicle = new VehicleResponse(Guid.NewGuid(), request.CustomerId, request.Make, request.Model, request.Year, request.LicensePlate);
    await store.SaveVehicleAsync(vehicle, cancellationToken);
    return Results.Created($"/vehicles/{vehicle.VehicleId}", vehicle);
}).WithOpenApi();

app.MapGet("/customers", async (PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListCustomersAsync(cancellationToken))).WithOpenApi();

app.MapGet("/customers/{id:guid}", async (Guid id, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    var customer = await store.GetCustomerAsync(id, cancellationToken);
    return customer is null ? Results.NotFound() : Results.Ok(customer);
}).WithOpenApi();

app.MapPut("/customers/{id:guid}", async (Guid id, CustomerRequest request, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.UpdateCustomerAsync(id, request, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapPatch("/customers/{id:guid}/active", async (Guid id, SetCustomerActiveRequest request, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.SetCustomerActiveAsync(id, request.IsActive, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapDelete("/customers/{id:guid}", async (Guid id, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteCustomerAsync(id, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapGet("/vehicles", async (PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListVehiclesAsync(cancellationToken))).WithOpenApi();

app.MapGet("/vehicles/{id:guid}", async (Guid id, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    var vehicle = await store.GetVehicleAsync(id, cancellationToken);
    return vehicle is null ? Results.NotFound() : Results.Ok(vehicle);
}).WithOpenApi();

app.MapPut("/vehicles/{id:guid}", async (Guid id, VehicleRequest request, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.UpdateVehicleAsync(id, request, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();
app.MapDelete("/vehicles/{id:guid}", async (Guid id, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.DeleteVehicleAsync(id, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapGet("/service-orders/public-status", async (string document, string licensePlate, PostgresServiceOrderStore store, CancellationToken cancellationToken) =>
{
    var result = await store.GetPublicStatusAsync(document, licensePlate, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).AllowAnonymous().WithOpenApi();

app.MapGet("/service-orders/{id:guid}", async (
    Guid id,
    IServiceOrderStore store,
    CancellationToken cancellationToken) =>
{
    var order = await store.GetAsync(id, cancellationToken);
    return order is null ? Results.NotFound() : Results.Ok(order);
}).WithOpenApi();

app.MapPost("/service-orders/{id:guid}/cancel", async (Guid id, CancelServiceOrderRequest request, IServiceOrderStore store, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Reason))
        return Results.BadRequest(new { error = "Motivo do cancelamento é obrigatório." });
    await store.ChangeLifecycleStatusAsync(id, "Canceled", request.Reason, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapPost("/service-orders/{id:guid}/deliver", async (Guid id, IServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.ChangeLifecycleStatusAsync(id, "Delivered", null, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.MapPost("/service-orders/{id:guid}/close", async (Guid id, IServiceOrderStore store, CancellationToken cancellationToken) =>
{
    await store.ChangeLifecycleStatusAsync(id, "Closed", null, cancellationToken);
    return Results.NoContent();
}).WithOpenApi();

app.Run();

public sealed record OpenServiceOrderRequest(
    Guid CustomerId,
    Guid VehicleId,
    decimal Amount,
    string? ServiceOrderNumber = null,
    IReadOnlyList<ServiceOrderServiceItemRequest>? ServiceItems = null,
    IReadOnlyList<ServiceOrderProductItemRequest>? PartItems = null);
public sealed record ServiceOrderServiceItemRequest(Guid ServiceId, int Quantity);
public sealed record ServiceOrderProductItemRequest(Guid ProductId, int Quantity);
public sealed record CustomerRequest(string Name, string Email, string PhoneNumber, string Document);
public sealed record CustomerResponse(Guid CustomerId, string Name, string Email, string PhoneNumber, string Document, bool IsActive);
public sealed record VehicleRequest(Guid CustomerId, string Make, string Model, int Year, string LicensePlate);
public sealed record VehicleResponse(Guid VehicleId, Guid CustomerId, string Make, string Model, int Year, string LicensePlate);
public sealed record CancelServiceOrderRequest(string Reason);
public sealed record SetCustomerActiveRequest(bool IsActive);
public sealed record PublicServiceOrderStatusResponse(string ServiceOrderNumber, string Status, DateTimeOffset CreatedAt);
public sealed record ServiceOrderResponse(Guid ServiceOrderId, string Status, Guid CorrelationId);
public sealed record ServiceOrderEvent(string EventType, Guid EventId, Guid CorrelationId, Guid ServiceOrderId, string? Payload = null);

public interface IServiceOrderStore
{
    Task CreateAsync(Guid id, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken);
    Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken);
    Task<ServiceOrderResponse?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task AddOutboxAsync(ServiceOrderEvent message, CancellationToken cancellationToken);
    Task<IReadOnlyList<(long Id, ServiceOrderEvent Message)>> GetPendingOutboxAsync(CancellationToken cancellationToken);
    Task MarkOutboxPublishedAsync(long id, CancellationToken cancellationToken);
    Task SaveCustomerAsync(CustomerResponse customer, CancellationToken cancellationToken);
    Task SaveVehicleAsync(VehicleResponse vehicle, CancellationToken cancellationToken);
    Task<bool> CustomerExistsAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> VehicleBelongsToCustomerAsync(Guid id, Guid customerId, CancellationToken cancellationToken);
    Task ChangeLifecycleStatusAsync(Guid id, string status, string? reason, CancellationToken cancellationToken);
}

public sealed class PostgresServiceOrderStore(IConfiguration configuration) : IServiceOrderStore
{
    private readonly string _connectionString = configuration.GetConnectionString("ServiceOrders")
        ?? throw new InvalidOperationException("ConnectionStrings:ServiceOrders não foi configurada.");

    public async Task<IReadOnlyList<CustomerResponse>> ListCustomersAsync(CancellationToken cancellationToken)
    {
        var result = new List<CustomerResponse>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,email,phone,document,is_active FROM customers ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new CustomerResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task<CustomerResponse?> GetCustomerAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,name,email,phone,document,is_active FROM customers WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CustomerResponse(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5))
            : null;
    }

    public async Task UpdateCustomerAsync(Guid id, CustomerRequest request, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customers SET name=@name,email=@email,phone=@phone,document=@document WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", request.Name);
        command.Parameters.AddWithValue("email", request.Email);
        command.Parameters.AddWithValue("phone", request.PhoneNumber);
        command.Parameters.AddWithValue("document", request.Document);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Cliente não encontrado.");
    }

    public async Task SetCustomerActiveAsync(Guid id, bool isActive, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customers SET is_active=@active WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("active", isActive);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Cliente não encontrado.");
    }

    public async Task DeleteCustomerAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM customers WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Cliente não encontrado.");
    }

    public async Task<IReadOnlyList<VehicleResponse>> ListVehiclesAsync(CancellationToken cancellationToken)
    {
        var result = new List<VehicleResponse>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,customer_id,make,model,year,license_plate FROM vehicles ORDER BY license_plate";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(new VehicleResponse(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
        return result;
    }

    public async Task<VehicleResponse?> GetVehicleAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,customer_id,make,model,year,license_plate FROM vehicles WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new VehicleResponse(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5))
            : null;
    }

    public async Task UpdateVehicleAsync(Guid id, VehicleRequest request, CancellationToken cancellationToken)
    {
        if (!await CustomerExistsAsync(request.CustomerId, cancellationToken)) throw new KeyNotFoundException("Cliente não encontrado.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE vehicles SET customer_id=@customer,make=@make,model=@model,year=@year,license_plate=@plate WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("customer", request.CustomerId);
        command.Parameters.AddWithValue("make", request.Make);
        command.Parameters.AddWithValue("model", request.Model);
        command.Parameters.AddWithValue("year", request.Year);
        command.Parameters.AddWithValue("plate", request.LicensePlate);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Veículo não encontrado.");
    }

    public async Task DeleteVehicleAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM vehicles WHERE id=@id";
        command.Parameters.AddWithValue("id", id);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0) throw new KeyNotFoundException("Veículo não encontrado.");
    }

    public async Task<PublicServiceOrderStatusResponse?> GetPublicStatusAsync(string document, string licensePlate, CancellationToken cancellationToken)
    {
        var normalizedPlate = licensePlate.Replace("-", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT so.service_order_number, so.status, now()
            FROM service_orders so
            JOIN customers c ON c.id=so.customer_id
            JOIN vehicles v ON v.id=so.vehicle_id
            WHERE c.document=@document
              AND replace(replace(upper(v.license_plate),'-',''),' ','')=@plate
            ORDER BY so.id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("document", document);
        command.Parameters.AddWithValue("plate", normalizedPlate);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PublicServiceOrderStatusResponse(reader.IsDBNull(0) ? string.Empty : reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2))
            : null;
    }

    public async Task CreateAsync(Guid id, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS service_orders (
                id uuid PRIMARY KEY,
                service_order_number varchar(50),
                customer_id uuid NOT NULL,
                vehicle_id uuid NOT NULL,
                amount numeric(18,2) NOT NULL,
                status varchar(40) NOT NULL,
                correlation_id uuid NOT NULL
            );
            ALTER TABLE service_orders ADD COLUMN IF NOT EXISTS service_order_number varchar(50);
            CREATE TABLE IF NOT EXISTS customers (
                id uuid PRIMARY KEY, name varchar(200) NOT NULL, email varchar(320) NOT NULL,
                phone varchar(40) NOT NULL, document varchar(40) NOT NULL UNIQUE, is_active boolean NOT NULL
            );
            CREATE TABLE IF NOT EXISTS vehicles (
                id uuid PRIMARY KEY, customer_id uuid NOT NULL REFERENCES customers(id),
                make varchar(100) NOT NULL, model varchar(100) NOT NULL, year int NOT NULL,
                license_plate varchar(20) NOT NULL UNIQUE
            );
            CREATE TABLE IF NOT EXISTS outbox_messages (
                id bigserial PRIMARY KEY,
                event_type varchar(100) NOT NULL,
                event_id uuid NOT NULL UNIQUE,
                correlation_id uuid NOT NULL,
                service_order_id uuid NOT NULL,
                payload jsonb,
                published_at timestamptz NULL
            );
            INSERT INTO service_orders(id, service_order_number, customer_id, vehicle_id, amount, status, correlation_id)
            VALUES (@id, @number, @customer_id, @vehicle_id, @amount, 'PendingBilling', @correlation_id);
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("number", (object?)request.ServiceOrderNumber ?? id.ToString("N"));
        command.Parameters.AddWithValue("customer_id", request.CustomerId);
        command.Parameters.AddWithValue("vehicle_id", request.VehicleId);
        command.Parameters.AddWithValue("amount", request.Amount);
        command.Parameters.AddWithValue("correlation_id", correlationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveCustomerAsync(CustomerResponse customer, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO customers(id,name,email,phone,document,is_active) VALUES (@id,@name,@email,@phone,@document,@active)";
        command.Parameters.AddWithValue("id", customer.CustomerId);
        command.Parameters.AddWithValue("name", customer.Name);
        command.Parameters.AddWithValue("email", customer.Email);
        command.Parameters.AddWithValue("phone", customer.PhoneNumber);
        command.Parameters.AddWithValue("document", customer.Document);
        command.Parameters.AddWithValue("active", customer.IsActive);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveVehicleAsync(VehicleResponse vehicle, CancellationToken cancellationToken)
    {
        if (!await CustomerExistsAsync(vehicle.CustomerId, cancellationToken))
            throw new InvalidOperationException("Cliente não encontrado.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO vehicles(id,customer_id,make,model,year,license_plate) VALUES (@id,@customer,@make,@model,@year,@plate)";
        command.Parameters.AddWithValue("id", vehicle.VehicleId);
        command.Parameters.AddWithValue("customer", vehicle.CustomerId);
        command.Parameters.AddWithValue("make", vehicle.Make);
        command.Parameters.AddWithValue("model", vehicle.Model);
        command.Parameters.AddWithValue("year", vehicle.Year);
        command.Parameters.AddWithValue("plate", vehicle.LicensePlate);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CustomerExistsAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM customers WHERE id=@id AND is_active)";
        command.Parameters.AddWithValue("id", id);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task<bool> VehicleBelongsToCustomerAsync(Guid id, Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM vehicles WHERE id=@id AND customer_id=@customer)";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("customer", customerId);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
    }

    public async Task AddOutboxAsync(ServiceOrderEvent message, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS outbox_messages (
                id bigserial PRIMARY KEY, event_type varchar(100) NOT NULL,
                event_id uuid NOT NULL UNIQUE, correlation_id uuid NOT NULL,
                service_order_id uuid NOT NULL, payload jsonb, published_at timestamptz NULL
            );
            INSERT INTO outbox_messages(event_type, event_id, correlation_id, service_order_id, payload)
            VALUES (@type, @event, @correlation, @order, CAST(@payload AS jsonb))
            ON CONFLICT (event_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("type", message.EventType);
        command.Parameters.AddWithValue("event", message.EventId);
        command.Parameters.AddWithValue("correlation", message.CorrelationId);
        command.Parameters.AddWithValue("order", message.ServiceOrderId);
        command.Parameters.AddWithValue("payload", (object?)message.Payload ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(long Id, ServiceOrderEvent Message)>> GetPendingOutboxAsync(CancellationToken cancellationToken)
    {
        var result = new List<(long, ServiceOrderEvent)>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, event_type, event_id, correlation_id, service_order_id, payload FROM outbox_messages WHERE published_at IS NULL ORDER BY id LIMIT 100";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add((reader.GetInt64(0), new ServiceOrderEvent(reader.GetString(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.IsDBNull(5) ? null : reader.GetString(5))));
        }
        return result;
    }

    public async Task MarkOutboxPublishedAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE outbox_messages SET published_at = now() WHERE id = @id";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetStatusAsync(Guid id, string status, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE service_orders SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ChangeLifecycleStatusAsync(Guid id, string status, string? reason, CancellationToken cancellationToken)
    {
        var current = await GetAsync(id, cancellationToken)
            ?? throw new KeyNotFoundException("Ordem de serviço não encontrada.");
        var allowed = (current.Status, status) switch
        {
            ("Finished", "Delivered") => true,
            ("Delivered", "Closed") => true,
            ("Canceled", _) => false,
            (_, "Canceled") => true,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException($"Transição de {current.Status} para {status} não permitida.");
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE service_orders SET status=@status WHERE id=@id";
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ServiceOrderResponse?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT status, correlation_id FROM service_orders WHERE id = @id";
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ServiceOrderResponse(id, reader.GetString(0), reader.GetGuid(1))
            : null;
    }
}

public interface ISagaParticipantClient
{
    Task RequestQuoteAsync(Guid serviceOrderId, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken);
}

public sealed class SagaParticipantClient(HttpClient httpClient, IConfiguration configuration) : ISagaParticipantClient
{
    public async Task RequestQuoteAsync(Guid serviceOrderId, OpenServiceOrderRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        var url = configuration["Services:BillingUrl"]
            ?? throw new InvalidOperationException("Services:BillingUrl não foi configurada.");
        using var response = await httpClient.PostAsJsonAsync(
            $"{url.TrimEnd('/')}/quotes",
            new { serviceOrderId, request.Amount, request.ServiceOrderNumber, request.ServiceItems, request.PartItems, correlationId },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

public sealed class OpenServiceOrderSaga(IServiceOrderStore store, ISagaParticipantClient billing)
{
    public async Task<ServiceOrderResponse> StartAsync(OpenServiceOrderRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        await store.CreateAsync(id, request, correlationId, cancellationToken);
        await store.AddOutboxAsync(new ServiceOrderEvent("ServiceOrderOpened", Guid.NewGuid(), correlationId, id), cancellationToken);
        try
        {
            await billing.RequestQuoteAsync(id, request, correlationId, cancellationToken);
            await store.AddOutboxAsync(new ServiceOrderEvent("QuoteRequested", Guid.NewGuid(), correlationId, id), cancellationToken);
            return new ServiceOrderResponse(id, "PendingApproval", correlationId);
        }
        catch
        {
            await store.SetStatusAsync(id, "BillingFailed", cancellationToken);
            throw;
        }
    }

}

public interface IEventBus
    {
        Task PublishAsync(ServiceOrderEvent message, CancellationToken cancellationToken);
    }

    public sealed class RabbitMqEventBus : IEventBus, IDisposable
    {
        private readonly IConnection _connection;
        private readonly IModel _channel;

        public RabbitMqEventBus(IConfiguration configuration)
        {
            _connection = CreateConnection(configuration);
            _channel = _connection.CreateModel();
            _channel.ExchangeDeclare("garage.events", ExchangeType.Topic, durable: true);
        }

        public Task PublishAsync(ServiceOrderEvent message, CancellationToken cancellationToken)
        {
            var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
            _channel.BasicPublish("garage.events", message.EventType, null, body);
            return Task.CompletedTask;
        }

        private static IConnection CreateConnection(IConfiguration configuration)
        {
            var factory = new ConnectionFactory
            {
                Uri = new Uri(configuration["RabbitMq:Uri"] ?? "amqp://guest:guest@localhost:5672/")
            };
            return factory.CreateConnection();
        }

        public void Dispose()
        {
            _channel.Dispose();
            _connection.Dispose();
        }
    }

    public sealed class OutboxPublisher(IServiceOrderStore store, IEventBus bus, ILogger<OutboxPublisher> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    foreach (var item in await store.GetPendingOutboxAsync(stoppingToken))
                    {
                        await bus.PublishAsync(item.Message, stoppingToken);
                        await store.MarkOutboxPublishedAsync(item.Id, stoppingToken);
                    }
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Falha ao publicar mensagens da Outbox.");
                }
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }

public sealed class SagaEventConsumer(IServiceOrderStore store, IHttpClientFactory clients, IConfiguration configuration, ILogger<SagaEventConsumer> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                var factory = new ConnectionFactory { Uri = new Uri(configuration["RabbitMq:Uri"] ?? "amqp://guest:guest@localhost:5672/") };
                using var connection = factory.CreateConnection();
                using var channel = connection.CreateModel();
                channel.ExchangeDeclare("garage.events", ExchangeType.Topic, durable: true);
                var queue = channel.QueueDeclare("os-service-saga", durable: true, exclusive: false, autoDelete: false).QueueName;
                channel.QueueBind(queue, "garage.events", "QuoteApproved");
                channel.QueueBind(queue, "garage.events", "PaymentApproved");
                channel.QueueBind(queue, "garage.events", "PaymentRejected");
                channel.QueueBind(queue, "garage.events", "ExecutionCompleted");
                var consumer = new EventingBasicConsumer(channel);
                consumer.Received += async (_, args) =>
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<ServiceOrderEvent>(args.Body.ToArray());
                        if (message is null) return;
                        if (message.EventType == "QuoteApproved")
                        {
                            await store.SetStatusAsync(message.ServiceOrderId, "AwaitingPayment", stoppingToken);
                        }
                        else if (message.EventType == "PaymentApproved")
                        {
                            await store.SetStatusAsync(message.ServiceOrderId, "PendingExecution", stoppingToken);
                            var executionUrl = configuration["Services:ExecutionUrl"] ?? throw new InvalidOperationException("Services:ExecutionUrl não foi configurada.");
                            var response = await clients.CreateClient().PostAsJsonAsync($"{executionUrl.TrimEnd('/')}/executions", new { serviceOrderId = message.ServiceOrderId, correlationId = message.CorrelationId }, stoppingToken);
                            response.EnsureSuccessStatusCode();
                        }
                        else if (message.EventType == "PaymentRejected")
                        {
                            await store.SetStatusAsync(message.ServiceOrderId, "PaymentFailed", stoppingToken);
                        }
                        else if (message.EventType == "ExecutionCompleted")
                        {
                            await store.SetStatusAsync(message.ServiceOrderId, "Finished", stoppingToken);
                        }
                        channel.BasicAck(args.DeliveryTag, false);
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Falha ao processar evento da Saga.");
                        channel.BasicNack(args.DeliveryTag, false, true);
                    }
                };
                channel.BasicConsume(queue, autoAck: false, consumer);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Consumidor da Saga não conseguiu conectar ao RabbitMQ.");
            }
        }
    }
