## Updated Guide: .NET 10 Modular Monolith (DDD + Clean Architecture + BDD)

> **Core principle:** A modular monolith is a single deployable unit whose internal code is structured into **bounded-context modules** with well-defined contracts — as if they were separate services, but without the network boundary. (Milan Jovanović, ABP, Kamil Grzybek)

---

### Why Modular Monolith First?

| Signal | Stay Modular Monolith | Split to Microservice |
|--------|----------------------|----------------------|
| Team size | < 15–20 engineers | Multiple teams blocked on each other |
| Scaling | Uniform load | One module needs very different scaling |
| Deployment | Single cadence is fine | Independent release cadences required |
| Compliance | No isolation needed | PCI / GDPR requires network-level isolation |
| Technology | Same stack everywhere | A domain needs a different language/stack |

> *"Don't split because you can. Split because you need to."* — DEV Community, 2026

---

### Step 1 — Solution Scaffold (Module-First)

The structure follows **Kamil Grzybek's reference project** (`kgrzybek/modular-monolith-with-ddd`) and the **ABP modular monolith** pattern:

```
MyApi/
├── Directory.Packages.props
├── Directory.Build.props
├── sonar-project.properties
├── MyApi.slnx
│
├── MyApi.Host/                          # ← THE ONLY project that references ALL modules
│   ├── Program.cs
│   ├── appsettings.json
│   └── Middleware/
│
├── Modules/
│   ├── MyApi.Modules.Identity/          # Bounded Context: Identity
│   │   ├── Contracts/                   # ← Public API of the module (the ONLY thing other modules see)
│   │   │   ├── IIdentityService.cs
│   │   │   ├── Dtos/
│   │   │   └── Events/
│   │   ├── Domain/
│   │   ├── Application/
│   │   ├── Infrastructure/
│   │   ├── IdentityDbContext.cs         # Own DbContext → own schema
│   │   └── IdentityModule.cs           # Module registration (composition root)
│   │
│   ├── MyApi.Modules.Orders/            # Bounded Context: Orders
│   │   ├── Contracts/
│   │   ├── Domain/
│   │   ├── Application/
│   │   ├── Infrastructure/
│   │   ├── OrdersDbContext.cs
│   │   └── OrdersModule.cs
│   │
│   └── MyApi.Modules.Catalog/           # Bounded Context: Catalog (add later)
│       ├── Contracts/
│       ├── Domain/
│       ├── Application/
│       ├── Infrastructure/
│       ├── CatalogDbContext.cs
│       └── CatalogModule.cs
│
├── Shared/
│   ├── MyApi.Shared.Kernel/             # Entity base, ValueObject, DomainException
│   └── MyApi.Shared.EventBus/           # In-process event bus abstraction
│
├── Tests/
│   ├── MyApi.Modules.Identity.Tests/
│   ├── MyApi.Modules.Orders.Tests/
│   ├── MyApi.Modules.Catalog.Tests/
│   ├── MyApi.AcceptanceTests/           # BDD (Reqnroll)
│   └── MyApi.Architecture.Tests/        # ArchUnitNET boundary enforcement
│
└── .github/workflows/ci.yml
```

**Create the projects:**

```bash
mkdir MyApi && cd MyApi
dotnet new sln -n MyApi

# Host (thin API shell)
dotnet new webapi -n MyApi.Host

# Shared kernel (minimal — NOT a god package)
dotnet new classlib -n Shared/MyApi.Shared.Kernel
dotnet new classlib -n Shared/MyApi.Shared.EventBus

# Module: Identity (start here)
dotnet new classlib -n Modules/MyApi.Modules.Identity
dotnet new classlib -n Modules/MyApi.Modules.Identity/Contracts
dotnet new classlib -n Modules/MyApi.Modules.Identity/Domain
dotnet new classlib -n Modules/MyApi.Modules.Identity/Application
dotnet new classlib -n Modules/MyApi.Modules.Identity/Infrastructure

# Module: Orders
dotnet new classlib -n Modules/MyApi.Modules.Orders
dotnet new classlib -n Modules/MyApi.Modules.Orders/Contracts
dotnet new classlib -n Modules/MyApi.Modules.Orders/Domain
dotnet new classlib -n Modules/MyApi.Modules.Orders/Application
dotnet new classlib -n Modules/MyApi.Modules.Orders/Infrastructure

# Tests
dotnet new xunit -n Tests/MyApi.Modules.Identity.Tests
dotnet new xunit -n Tests/MyApi.Modules.Orders.Tests
dotnet new xunit -n Tests/MyApi.AcceptanceTests
dotnet new xunit -n Tests/MyApi.Architecture.Tests

# Add all to solution
dotnet sln add MyApi.Host Shared/MyApi.Shared.Kernel Shared/MyApi.Shared.EventBus \
  Modules/MyApi.Modules.Identity Modules/MyApi.Modules.Identity/Contracts \
  Modules/MyApi.Modules.Identity/Domain Modules/MyApi.Modules.Identity/Application \
  Modules/MyApi.Modules.Identity/Infrastructure \
  Modules/MyApi.Modules.Orders Modules/MyApi.Modules.Orders/Contracts \
  Modules/MyApi.Modules.Orders/Domain Modules/MyApi.Modules.Orders/Application \
  Modules/MyApi.Modules.Orders/Infrastructure \
  Tests/MyApi.Modules.Identity.Tests Tests/MyApi.Modules.Orders.Tests \
  Tests/MyApi.AcceptanceTests Tests/MyApi.Architecture.Tests
```

---

### Step 2 — Enforce the Dependency Rule (Compiler + Tests)

**The two iron rules** (from `thecodeman.net` and ABP):

1. **The Host project is the only place that references all modules.** No module references another module's implementation.
2. **Modules talk to each other only through `*.Contracts` projects** — interfaces, DTOs, and integration events. Never EF Core entities, never internal services.

```bash
# Host references ALL modules
dotnet add MyApi.Host reference Modules/MyApi.Modules.Identity
dotnet add MyApi.Host reference Modules/MyApi.Modules.Orders

# Module internal references (top-down)
dotnet add Modules/MyApi.Modules.Identity/Infrastructure reference Modules/MyApi.Modules.Identity/Application
dotnet add Modules/MyApi.Modules.Identity/Application reference Modules/MyApi.Modules.Identity/Domain
dotnet add Modules/MyApi.Modules.Identity/Application reference Modules/MyApi.Modules.Identity/Contracts
dotnet add Modules/MyApi.Modules.Identity/Infrastructure reference Shared/MyApi.Shared.EventBus
dotnet add Modules/MyApi.Modules.Identity/Application reference Shared/MyApi.Shared.EventBus

# Cross-module: ONLY Contracts
dotnet add Modules/MyApi.Modules.Orders/Application reference Modules/MyApi.Modules.Identity/Contracts
dotnet add Modules/MyApi.Modules.Orders/Application reference Modules/MyApi.Modules.Catalog/Contracts

# Tests
dotnet add Tests/MyApi.Modules.Identity.Tests reference Modules/MyApi.Modules.Identity
dotnet add Tests/MyApi.Modules.Orders.Tests reference Modules/MyApi.Modules.Orders
dotnet add Tests/MyApi.AcceptanceTests reference MyApi.Host
dotnet add Tests/MyApi.Architecture.Tests reference MyApi.Host
```

> **Critical:** `MyApi.Modules.Orders/Domain` must NOT reference `MyApi.Modules.Identity/Domain`. If you need Identity data in Orders, you consume it via `Identity.Contracts` (an interface + DTO or an integration event).

---

### Step 3 — Module Registration (Composition Root per Module)

Each module has its own **composition root** — a static class that the Host calls during startup. This mirrors ABP's `AbpModule` pattern and Grzybek's per-module IoC.

```csharp
// Modules/MyApi.Modules.Orders/OrdersModule.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApi.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services,
        string connectionString)
    {
        // Own DbContext → own schema
        services.AddDbContext<OrdersDbContext>(opt =>
            opt.UseNpgsql(connectionString, sql => sql.MigrationsAssembly(typeof(OrdersModule).Assembly.FullName)));

        // Register handlers, services, event subscribers
        services.AddScoped<IOrderService, OrderService>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IIntegrationEventHandler<OrderConfirmedEvent>, InventoryEventHandler>();

        return services;
    }
}
```

```csharp
// Modules/MyApi.Modules.Identity/IdentityModule.cs
namespace MyApi.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<IdentityDbContext>(opt =>
            opt.UseNpgsql(connectionString, sql => sql.MigrationsAssembly(typeof(IdentityModule).Assembly.FullName)));

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IUserRepository, UserRepository>();

        return services;
    }
}
```

**Host `Program.cs` — the only place all modules meet:**

```csharp
// MyApi.Host/Program.cs
using MyApi.Modules.Identity;
using MyApi.Modules.Orders;
using MyApi.Shared.EventBus;

var builder = WebApplication.CreateBuilder(args);

var connStr = builder.Configuration.GetConnectionString("Default")!;

// Register each module (order matters only for DI resolution, not logic)
builder.Services.AddIdentityModule(connStr);
builder.Services.AddOrdersModule(connStr);

// Shared infrastructure
builder.Services.AddInProcessEventBus();   // swap for MassTransit/RabbitMQ later
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.MapOpenApi();

// ── Thin API routing: delegate to module services ──────────
var api = app.MapGroup("/api/v1");

api.MapGet("/orders/{id:guid}", async (Guid id, IOrderService orders, CancellationToken ct) =>
{
    var dto = await orders.GetOrderAsync(id, ct);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

api.MapPost("/orders", async (CreateOrderRequest req, IOrderService orders, CancellationToken ct) =>
{
    var id = await orders.CreateOrderAsync(req, ct);
    return Results.Created($"/api/v1/orders/{id}", null);
});

app.Run();
```

> **Why this matters for microservice extraction:** When you split `Orders` into a service, you create a new host project that calls `AddOrdersModule()`. The module code doesn't change. (Source: `antondevtips.com`, `dev.to/aldacosta`)

---

### Step 4 — Module Contracts (The Public API)

**This is the most critical part for future microservice extraction.** The `Contracts` project is the **only assembly** other modules (or a future external service) can reference.

```csharp
// Modules/MyApi.Modules.Identity/Contracts/IIdentityService.cs
namespace MyApi.Modules.Identity.Contracts;

public interface IIdentityService
{
    Task<UserDto?> GetUserByIdAsync(Guid id, CancellationToken ct = default);
    Task<UserDto> RegisterAsync(RegisterRequest req, CancellationToken ct = default);
}

// Modules/MyApi.Modules.Identity/Contracts/Dtos/UserDto.cs
namespace MyApi.Modules.Identity.Contracts.Dtos;

public record UserDto(Guid Id, string Email, string Name);

// Modules/MyApi.Modules.Identity/Contracts/Dtos/RegisterRequest.cs
namespace MyApi.Modules.Identity.Contracts.Dtos;

public record RegisterRequest(string Email, string Password, string Name);
```

**Integration events (published to the event bus):**

```csharp
// Modules/MyApi.Modules.Identity/Contracts/Events/UserRegisteredEvent.cs
namespace MyApi.Modules.Identity.Contracts.Events;

public record UserRegisteredEvent(Guid UserId, string Email, string Name);
```

```csharp
// Modules/MyApi.Modules.Orders/Contracts/Events/OrderConfirmedEvent.cs
namespace MyApi.Modules.Orders.Contracts.Events;

public record OrderConfirmedEvent(Guid OrderId, Guid UserId, decimal Total, DateTimeOffset At);
```

> **Rule:** If a type is in `Contracts`, it's a **public API**. Treat it like a versioned NuGet package. Breaking changes require a version bump. (Source: `thecodeman.net`, ABP modularity docs)

---

### Step 5 — In-Process Event Bus (Swappable to Broker)

The event bus is the **seam** between in-process (modular monolith) and network (microservices). Start in-process; swap to MassTransit/RabbitMQ when you extract.

```csharp
// Shared/MyApi.Shared.EventBus/IEventBus.cs
namespace MyApi.Shared.EventBus;

public interface IEventBus
{
    Task PublishAsync(object @event, CancellationToken ct = default);
    void Subscribe<TEvent, THandler>() where THandler : class, IIntegrationEventHandler<TEvent>;
}

public interface IIntegrationEventHandler<TEvent>
{
    Task HandleAsync(TEvent @event, CancellationToken ct = default);
}
```

```csharp
// Shared/MyApi.Shared.EventBus/InProcessEventBus.cs
using Microsoft.Extensions.DependencyInjection;

namespace MyApi.Shared.EventBus;

public class InProcessEventBus : IEventBus
{
    private readonly IServiceProvider _sp;
    private readonly Dictionary<Type, Type> _handlers = new();

    public InProcessEventBus(IServiceProvider sp) => _sp = sp;

    public void Subscribe<TEvent, THandler>()
        where THandler : class, IIntegrationEventHandler<TEvent>
        => _handlers[typeof(TEvent)] = typeof(THandler);

    public async Task PublishAsync(object @event, CancellationToken ct = default)
    {
        if (!_handlers.TryGetValue(@event.GetType(), out var handlerType)) return;

        // Resolve handler in a scoped context
        using var scope = _sp.CreateScope();
        var handler = (IIntegrationEventHandler<dynamic>)scope.ServiceProvider.GetRequiredService(handlerType);
        await handler.HandleAsync((dynamic)@event, ct);
    }
}

// Shared/MyApi.Shared.EventBus/ServiceCollectionExtensions.cs
namespace MyApi.Shared.EventBus;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInProcessEventBus(this IServiceCollection services)
    {
        services.AddSingleton<InProcessEventBus>();
        services.AddSingleton<IEventBus>(sp => sp.GetRequiredService<InProcessEventBus>());
        return services;
    }
}
```

**Consuming an event in another module:**

```csharp
// Modules/MyApi.Modules.Orders/Infrastructure/InventoryEventHandler.cs
using MyApi.Modules.Identity.Contracts.Events;
using MyApi.Shared.EventBus;

namespace MyApi.Modules.Orders.Infrastructure;

public class InventoryEventHandler : IIntegrationEventHandler<UserRegisteredEvent>
{
    private readonly ILogger<InventoryEventHandler> _logger;
    public InventoryEventHandler(ILogger<InventoryEventHandler> logger) => _logger = logger;

    public Task HandleAsync(UserRegisteredEvent @event, CancellationToken ct = default)
    {
        _logger.LogInformation("User {UserId} registered — provisioning defaults", @event.UserId);
        // e.g., create a default wallet, assign a tier, etc.
        return Task.CompletedTask;
    }
}
```

> **Extraction path:** When `Identity` becomes a microservice, you replace `InProcessEventBus` with `MassTransitEventBus` (same `IEventBus` interface). The handler code in `Orders` doesn't change. (Source: `dev.to/aldacosta`, `antondevtips.com`)

---

### Step 6 — Data Isolation (One Schema Per Module)

**The single most important rule for future extraction:**

> *"Separate schemas per module. One database, one schema per domain, no cross-schema joins. This is the boundary that matters most, because a shared table is the coupling that makes extraction impossible later."* — brocoders.com, 2026

```csharp
// Modules/MyApi.Modules.Orders/OrdersDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace MyApi.Modules.Orders;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");   // ← own schema
        // ... entity configurations
    }
}
```

```csharp
// Modules/MyApi.Modules.Identity/IdentityDbContext.cs
using Microsoft.EntityFrameworkCore;

namespace MyApi.Modules.Identity;

public class IdentityDbContext : DbContext
{
    public IdentityDbContext(DbContextOptions<IdentityDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("identity");  // ← own schema
    }
}
```

**Cross-module data access rules:**

| Scenario | ✅ Do | ❌ Don't |
|----------|------|---------|
| Orders needs a user's email | Subscribe to `UserRegisteredEvent` or call `IIdentityService.GetUserByIdAsync()` | `SELECT * FROM identity.users` in Orders |
| Orders needs product price | Call `ICatalogService.GetPriceAsync()` or subscribe to `ProductPricedEvent` | JOIN `catalog.products` in an Orders query |
| Reporting needs both | Create a **read model** (materialized view or projection) in a separate schema | Cross-schema JOIN |

> If you need a read-only view of another module's data, use EF Core's `ToView()` or a database view owned by the consuming module. (Source: `thereformedprogrammer.net`)

---

### Step 7 — Domain Layer (Per Module, DDD)

Same DDD principles as before, but **scoped to the module's bounded context**:

```
Modules/MyApi.Modules.Orders/Domain/
├── Order.cs                  (Aggregate Root)
├── OrderId.cs                (Value Object)
├── OrderStatus.cs
├── OrderItem.cs
├── IOrderRepository.cs       (Port)
└── Events/
    └── OrderConfirmedEvent.cs  (Domain event, internal to context)
```

```csharp
// Modules/MyApi.Modules.Orders/Domain/Order.cs
using MyApi.Shared.Kernel;

namespace MyApi.Modules.Orders.Domain;

public class Order : Entity
{
    private readonly List<OrderItem> _items = [];

    private Order() { }

    public Guid UserId { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.Pending;
    public DateTimeOffset PlacedAt { get; private set; } = DateTimeOffset.UtcNow;
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();
    public decimal Total => _items.Sum(i => i.Price * i.Quantity);

    public static Order Create(Guid userId, IReadOnlyCollection<OrderItem> items)
    {
        if (items is null || items.Count == 0)
            throw new DomainException("An order must have at least one item.");

        var order = new Order { UserId = userId };
        foreach (var item in items)
            order._items.Add(item);
        return order;
    }

    public void Confirm()
    {
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot confirm an order in {Status} state.");
        Status = OrderStatus.Confirmed;
        // Domain event (internal to this context)
        AddDomainEvent(new OrderConfirmedDomainEvent(Id, UserId, Total));
    }
}
```

**Key DDD rules per module:**
- Private setters, factory methods, invariants in the entity
- Domain events are **internal** to the context (not published externally)
- Integration events (in `Contracts/Events/`) are what other modules see
- No cross-context entity references — use IDs (e.g., `Guid UserId`, not `User user`)

---

### Step 8 — Application Layer (Use Cases + CQRS per Module)

```
Modules/MyApi.Modules.Orders/Application/
├── OrderService.cs           (implements IOrderService from Contracts)
├── Commands/
│   ├── CreateOrderCommand.cs
│   └── CreateOrderHandler.cs
├── Queries/
│   ├── GetOrderQuery.cs
│   └── GetOrderHandler.cs
├── EventHandlers/
│   └── UserRegisteredHandler.cs  (reacts to Identity events)
└── Validation/
    └── CreateOrderValidator.cs
```

```csharp
// Modules/MyApi.Modules.Orders/Application/OrderService.cs
using MyApi.Modules.Orders.Contracts;
using MyApi.Modules.Orders.Contracts.Dtos;
using MyApi.Shared.EventBus;
using FluentValidation;

namespace MyApi.Modules.Orders.Application;

public class OrderService : IOrderService
{
    private readonly IOrderRepository _orders;
    private readonly IApplicationDbContext _db;
    private readonly IEventBus _events;
    private readonly IValidator<CreateOrderCommand> _validator;

    public OrderService(IOrderRepository orders, IApplicationDbContext db,
        IEventBus events, IValidator<CreateOrderCommand> validator)
    {
        _orders = orders;
        _db = db;
        _events = events;
        _validator = validator;
    }

    public async Task<Guid> CreateOrderAsync(CreateOrderRequest req, CancellationToken ct)
    {
        var cmd = new CreateOrderCommand(req.UserId, req.Items);
        var result = await _validator.ValidateAsync(cmd, ct);
        if (!result.IsValid)
            throw new ValidationException(result.Errors);

        var order = Order.Create(cmd.UserId, cmd.Items.Select(i =>
            OrderItem.Create(i.Name, i.Price, i.Quantity)).ToList());

        await _orders.SaveAsync(order, ct);

        // Publish integration event (other modules react)
        await _events.PublishAsync(
            new Orders.Contracts.Events.OrderCreatedEvent(order.Id, order.UserId, order.Total), ct);

        return order.Id;
    }

    public async Task<OrderDto?> GetOrderAsync(Guid id, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

        return order is null ? null : new OrderDto(order.Id, order.UserId, order.Status, order.Total);
    }
}
```

> **Note:** The `IOrderService` interface is in `Contracts/`. The implementation is in `Application/`. Other modules (or the Host) only see the interface. This is the **facade pattern** that makes extraction mechanical. (Source: `kgrzybek/modular-monolith-with-ddd`, ABP)

---

### Step 9 — Architecture Tests (Enforce Boundaries in CI)

```csharp
// Tests/MyApi.Architecture.Tests/ModuleBoundaryTests.cs
using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MyApi.Architecture.Tests;

public class ModuleBoundaryTests
{
    private static readonly IArchitecture Arch =
        ArchDefinition.FromAssemblies(
            typeof(MyApi.Host.Program).Assembly,
            typeof(MyApi.Modules.Orders.OrdersModule).Assembly,
            typeof(MyApi.Modules.Identity.IdentityModule).Assembly);

    [Fact]
    public void Orders_Domain_Should_Not_Reference_Identity()
    {
        Types.InAssemblies(typeof(MyApi.Modules.Orders.Domain.Order).Assembly)
            .Should().NotHaveDependencyOn("MyApi.Modules.Identity.Domain");
    }

    [Fact]
    public void Orders_Should_Only_Reference_Identity_Contracts()
    {
        Types.InAssemblies(typeof(MyApi.Modules.Orders.OrdersModule).Assembly)
            .That().AreInNamespace("MyApi.Modules.Orders")
            .Should().OnlyHaveAccessToTypesIn(
                "MyApi.Modules.Orders",
                "MyApi.Modules.Identity.Contracts",
                "MyApi.Shared.Kernel",
                "MyApi.Shared.EventBus",
                "Microsoft.EntityFrameworkCore",
                "FluentValidation");
    }

    [Fact]
    public void No_Module_Should_Reference_Another_Module_Infrastructure()
    {
        Types.InAssemblies(typeof(MyApi.Modules.Orders.OrdersModule).Assembly)
            .Should().NotHaveDependencyOn("MyApi.Modules.Identity.Infrastructure");
    }

    [Fact]
    public void No_Cross_Schema_Joins()
    {
        // Custom test: scan EF Core queries for cross-schema joins
        // (or use a database-level test with a test DB)
    }

    [Fact]
    public void Host_Is_The_Only_Project_Referencing_All_Modules()
    {
        // Verify via project file analysis or ArchUnitNET
    }
}
```

> These tests **fail the build** if a developer accidentally adds a cross-module reference. This is the "compiler-level enforcement" that keeps the modular monolith modular. (Source: `ness.com`, `dev.to/nainikmehta`)

---

### Step 10 — BDD Acceptance Tests (Cross-Module Scenarios)

```gherkin
# Tests/MyApi.AcceptanceTests/Features/OrderFlow.feature
Feature: Order Flow
  As a customer
  I want to place an order and have it confirmed
  So that I can track my purchase

  Scenario: Place and confirm an order
    Given a registered user "test@example.com"
    When I POST /api/v1/orders with user "test@example.com" and 2 items
    Then the response status should be 201
    And an OrderCreatedEvent should be published
    When I PUT /api/v1/orders/{id}/confirm
    Then the response status should be 204
    And an OrderConfirmedEvent should be published

  Scenario: Order creation fails for unregistered user
    Given no user exists with id "00000000-0000-0000-0000-000000000001"
    When I POST /api/v1/orders with that user and 1 item
    Then the response status should be 400
```

```csharp
// Tests/MyApi.AcceptanceTests/StepDefinitions/OrderFlowSteps.cs
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Reqnroll;

namespace MyApi.AcceptanceTests.StepDefinitions;

[Binding]
public class OrderFlowSteps
{
    private readonly ScenarioContext _ctx;
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly List<object> _publishedEvents = [];

    public OrderFlowSteps(ScenarioContext ctx,
        WebApplicationFactory<Program> factory)
    {
        _ctx = ctx;
        _factory = factory;
        _client = factory.CreateClient();
    }

    [BeforeScenario]
    public void Setup() => _publishedEvents.Clear();

    [Given(@"a registered user ""(.+)"")]
    public async Task GivenRegisteredUser(string email)
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/identity/register",
            new { Email = email, Password = "Pass123!", Name = "Test" });
        resp.EnsureSuccessStatusCode();
    }

    [When(@"I POST /api/v1/orders with user ""(.+)"" and (\d+) items")]
    public async Task WhenICreateOrder(string email, int itemCount)
    {
        var userId = await GetUserIdAsync(email);
        var items = Enumerable.Range(1, itemCount)
            .Select(i => new { Name = $"Item {i}", Price = 10.0m, Quantity = 1 }).ToList();

        var resp = await _client.PostAsJsonAsync("/api/v1/orders",
            new { UserId = userId, Items = items });
        _ctx.Set("response", resp);
    }

    [Then("an OrderCreatedEvent should be published")]
    public void ThenOrderCreatedEventPublished()
    {
        _publishedEvents.OfType<OrderCreatedEvent>().Should().ContainSingle();
    }
}
```

---

### Step 11 — Code Quality Metrics (Same Targets, Module-Aware)

The metrics from the previous guide apply **per module**, not just globally:

| Metric | Scope | Target | Enforcement |
|--------|-------|--------|-------------|
| Cyclomatic Complexity | Per method | < 10 | SonarQube |
| Cognitive Complexity | Per method | < 15 | SonarQube |
| Maintainability Index | Per file | > 65 | SonarQube |
| CBO | Per class | < 5 | SonarQube |
| DIT | Per class | < 5 | SonarQube |
| **Cross-Module Coupling** | Per module | < 3 contracts referenced | ArchUnitNET test |
| **Blast Radius** | Per module | < 2 modules affected | ArchUnitNET + Sonar |
| Code Duplication | Per module | < 5% | SonarQube |
| Mutation Score | Per module | > 70% | Stryker.NET |
| Dependency Depth | Per module | < 3 hops | ArchUnitNET |

**Additional modular-monolith-specific metrics:**

| Metric | What It Measures | Target |
|--------|-----------------|--------|
| **Module Boundary Violations** | Cross-module type references outside Contracts | 0 (CI gate) |
| **Schema Coupling** | Cross-schema JOINs in queries | 0 |
| **Contract Stability** | Breaking changes to `Contracts/` per release | 0 without version bump |
| **Event Bus Latency** | Time from publish to handler completion (in-process) | < 5ms p99 |

---

### Step 12 — CI/CD Pipeline

```yaml
name: CI
on: [push, pull_request]

jobs:
  build-test-analyze:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - run: dotnet restore

      - name: Build
        run: dotnet build --no-restore --configuration Release

      - name: Architecture Tests (boundary enforcement)
        run: dotnet test Tests/MyApi.Architecture.Tests --no-build --configuration Release

      - name: Module Unit Tests
        run: |
          dotnet test Tests/MyApi.Modules.Identity.Tests --no-build --configuration Release
          dotnet test Tests/MyApi.Modules.Orders.Tests --no-build --configuration Release

      - name: BDD Acceptance Tests
        run: dotnet test Tests/MyApi.AcceptanceTests --no-build --configuration Release

      - name: Mutation Testing (per module)
        run: |
          cd Tests/MyApi.Modules.Orders.Tests
          dotnet stryker --break-at 70

      - name: SonarQube Scan
        uses: sonarsource/sonarqube-scan-action@4
        env:
          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
          SONAR_HOST_URL: ${{ secrets.SONAR_HOST_URL }}
```

---

### Step 13 — Incremental Growth Guideline

This is the **operational playbook** for growing the modular monolith over time.

#### Phase 1: Foundation (Weeks 1–3)

| Action | Rationale |
|--------|-----------|
| Create `Shared.Kernel` (Entity, ValueObject, DomainException) | Minimal shared code — **not** a god package |
| Create `Shared.EventBus` (in-process) | The seam for future broker |
| Build **one** module (e.g., `Identity`) end-to-end | Prove the pattern before replicating |
| Write architecture tests for that module | Enforce boundaries from day 1 |
| Set up SonarQube + Stryker in CI | Metrics from the start, not retrofitted |

> *"Start with fewer, larger contexts and split when the language or the team structure forces it."* — Milan Jovanović

#### Phase 2: First Business Module (Weeks 4–6)

| Action | Rationale |
|--------|-----------|
| Add `Orders` module | Second bounded context |
| Define `Orders.Contracts` (IOrderService, DTOs, events) | Public API |
| Implement `OrdersDbContext` with schema `"orders"` | Data isolation |
| Wire `UserRegisteredEvent` → Orders handler | Cross-module integration via events |
| Add BDD scenarios for cross-module flows | Prove integration works |
| Update architecture tests for new boundaries | CI gate |

#### Phase 3: Add Modules as Needs Emerge (Ongoing)

**When to add a new module:**
- A new **bounded context** emerges from the business (e.g., `Catalog`, `Payments`, `Notifications`)
- The team exceeds ~8 people and needs independent ownership
- A domain has distinct scaling or compliance needs

**When NOT to add a module:**
- It's a technical concern (logging, caching) → put in `Shared/` or a cross-cutting middleware
- Two "modules" share 80% of their entities → they're the same context, merge them
- You're splitting to "fix" slow builds or poor code quality → that's a discipline problem, not an architecture problem

**Checklist for adding a module:**

```
□ Define the bounded context (ubiquitous language, events, commands)
□ Create Contracts/ project (interfaces, DTOs, events)
□ Create Domain/ (entities, value objects, repository ports)
□ Create Application/ (handlers, services, validators)
□ Create Infrastructure/ (DbContext, repository impl, event subscribers)
□ Create [Module]DbContext.cs with its own schema
□ Create [Module]Module.cs (composition root)
□ Register in Host Program.cs
□ Add architecture tests for new boundaries
□ Add unit tests + BDD scenarios
□ Update SonarQube project structure
```

#### Phase 4: Extract to Microservice (Only When Justified)

**Decision criteria** (all must be true):

1. ✅ The module already has its own DbContext and schema
2. ✅ All cross-module communication goes through Contracts (interfaces + events)
3. ✅ There is a **measurable, recurring pain** (independent scaling, compliance, team blocking)
4. ✅ The module's contracts are stable (no breaking changes in the last 2 sprints)

**Extraction steps** (from `antondevtips.com` and `thecodeman.net`):

```
1. Create a new host project: MyApi.Modules.Orders.Host
2. Move OrdersModule registration into the new host
3. Point OrdersDbContext at a separate database (change connection string)
4. Replace InProcessEventBus with MassTransit/RabbitMQ in both hosts
5. Replace IOrderService (in-process) with an HTTP/gRPC client in the monolith
6. Add resilience: retries, timeouts, circuit breakers
7. Add health checks, distributed tracing
8. Deploy the new service
9. Remove the Orders module from the monolith host
10. Run the monolith with the Orders module removed → verify no failures
```

> *"Migrate one module at a time. Tighten module boundaries and data ownership first, then extract a low-coupling module into its own project and database."* — Milan Jovanović

---

### Step 14 — Anti-Patterns to Avoid

| Anti-Pattern | Why It's Bad | Fix |
|-------------|-------------|-----|
| **God `Shared/` package** | Becomes the monolith hiding place; everything depends on it | Keep it to < 5 types (Entity, ValueObject, DomainException, EventBus interface) |
| **Cross-module JOINs** | Hardest coupling to break; makes extraction impossible | Use events or explicit service calls |
| **Direct entity sharing** | `Orders` holds a `User` object from Identity | Use `Guid UserId` + fetch via `IIdentityService` |
| **Module references another module's Application** | Breaks encapsulation; changes in one module's use cases break the other | Only reference `*.Contracts` |
| **Premature microservice split** | Operational overhead without benefit | Wait for measurable pain |
| **Shared database tables** | Two modules own the same table → neither can evolve independently | One table = one module owner |
| **Synchronous calls for "facts"** | Tightens coupling; a slow Identity service blocks Orders | Use events for notifications; sync calls only for queries |

---

### Summary: The Evolution Path

```
Phase 1 (Monolith)
  └── 1 module, 1 schema, in-process events
       ↓  (business grows, second context emerges)
Phase 2 (Modular Monolith)
  └── N modules, N schemas, in-process event bus
       ↓  (measurable pain: scaling, compliance, team blocking)
Phase 3 (Partial Extraction)
  └── N-1 modules in monolith + 1 extracted service
       ↓  (more pain, more boundaries proven)
Phase 4 (Microservices — only if needed)
  └── Multiple services, message broker, API gateway
```

**The key insight from all sources:** A well-built modular monolith makes extraction **mechanical**, not a rewrite. The contracts, schemas, and event bus are the seams. If you hold the discipline, the split is a deployment change, not a code change.

