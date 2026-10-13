## Step-by-Step Guide: .NET 10 REST API with DDD, Clean Architecture & BDD

---

### Step 1 — Solution & Project Scaffold

**Prerequisites:** .NET 10 SDK (`dotnet --version`), Docker Desktop (for PostgreSQL via Aspire).

```bash
mkdir MyApi && cd MyApi

# Solution
dotnet new sln -n MyApi

# Four architecture layers
dotnet new classlib -n MyApi.Domain
dotnet new classlib -n MyApi.Application
dotnet new classlib -n MyApi.Infrastructure
dotnet new webapi   -n MyApi.Api

# Test projects
dotnet new xunit    -n MyApi.Domain.Tests
dotnet new xunit    -n MyApi.Application.Tests
dotnet new xunit    -n MyApi.AcceptanceTests   # BDD lives here

# Add to solution
dotnet sln add MyApi.Domain MyApi.Application MyApi.Infrastructure MyApi.Api \
               MyApi.Domain.Tests MyApi.Application.Tests MyApi.AcceptanceTests
```

**Enforce the Dependency Rule with project references:**

```bash
# Application → Domain
dotnet add MyApi.Application reference MyApi.Domain

# Infrastructure → Application
dotnet add MyApi.Infrastructure reference MyApi.Application

# API → Application + Infrastructure
dotnet add MyApi.Api reference MyApi.Application
dotnet add MyApi.Api reference MyApi.Infrastructure

# Test projects
dotnet add MyApi.Domain.Tests       reference MyApi.Domain
dotnet add MyApi.Application.Tests  reference MyApi.Application
dotnet add MyApi.AcceptanceTests    reference MyApi.Api
```

> **The rule:** `Api → Infrastructure → Application → Domain`. Domain references **nothing**. The compiler enforces this — if you try to add a reverse reference, the build fails.

---

### Step 2 — Central Package Management & Shared Build Settings

Create `Directory.Packages.props` at the solution root:

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.*" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.*" />
    <PackageVersion Include="FluentValidation" Version="12.*" />
    <PackageVersion Include="FluentValidation.DependencyInjectionExtensions" Version="12.*" />
    <PackageVersion Include="Reqnroll.xUnit" Version="2.*" />
    <PackageVersion Include="Reqnroll.Tools.MsBuild.Generation" Version="2.*" />
    <PackageVersion Include="FluentAssertions" Version="8.*" />
    <PackageVersion Include="Microsoft.Extensions.DependencyInjection" Version="10.*" />
    <PackageVersion Include="Microsoft.Extensions.Http" Version="10.*" />
    <PackageVersion Include="NSubstitute" Version="5.*" />
    <PackageVersion Include="ArchUnitNET" Version="1.*" />
  </ItemGroup>
</Project>
```

Create `Directory.Build.props` for shared compiler settings:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
  </PropertyGroup>
</Project>
```

---

### Step 3 — Domain Layer (DDD)

**No NuGet packages. No project references. Pure C#.**

```
MyApi.Domain/
├── Common/
│   ├── Entity.cs
│   ├── ValueObject.cs
│   └── DomainException.cs
├── Orders/
│   ├── Order.cs              (Aggregate Root)
│   ├── OrderId.cs            (Value Object)
│   ├── OrderStatus.cs        (Enum)
│   ├── OrderItem.cs
│   └── IOrderRepository.cs   (Port — defined here, implemented in Infrastructure)
└── Events/
    └── IDomainEvent.cs
```

**Entity base with UUID v7:**

```csharp
// MyApi.Domain/Common/Entity.cs
namespace MyApi.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
}
```

**Domain exception:**

```csharp
// MyApi.Domain/Common/DomainException.cs
namespace MyApi.Domain.Common;

public class DomainException : Exception
{
    public DomainException(string message) : base(message) { }
}
```

**Aggregate root with rich domain logic (not anemic):**

```csharp
// MyApi.Domain/Orders/Order.cs
namespace MyApi.Domain.Orders;

public class Order : Entity
{
    private readonly List<OrderItem> _items = [];

    // Private constructor — enforce factory method
    private Order() { }

    public OrderStatus Status { get; private set; } = OrderStatus.Pending;
    public DateTimeOffset PlacedAt { get; private set; } = DateTimeOffset.UtcNow;

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public decimal Total => _items.Sum(i => i.Price * i.Quantity);

    // Factory method — encapsulates creation invariants
    public static Order Create(IReadOnlyCollection<OrderItem> items)
    {
        if (items is null || items.Count == 0)
            throw new DomainException("An order must have at least one item.");

        var order = new Order();
        foreach (var item in items)
            order._items.Add(item);

        return order;
    }

    public void Confirm()
    {
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot confirm an order in {Status} state.");
        Status = OrderStatus.Confirmed;
    }

    public void Cancel(string reason)
    {
        if (Status is OrderStatus.Shipped or OrderStatus.Delivered)
            throw new DomainException("Cannot cancel a shipped or delivered order.");
        Status = OrderStatus.Cancelled;
    }
}
```

**Repository port (interface in Domain, implementation in Infrastructure):**

```csharp
// MyApi.Domain/Orders/IOrderRepository.cs
namespace MyApi.Domain.Orders;

public interface IOrderRepository
{
    Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task SaveAsync(Order order, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct = default);
}
```

> **DDD rules that keep complexity low:** private setters, factory methods, invariants enforced in the entity. This keeps cyclomatic complexity < 10 per method because each method does one thing.

---

### Step 4 — Application Layer (Use Cases + CQRS)

```
MyApi.Application/
├── Common/
│   ├── IApplicationDbContext.cs
│   └── Result.cs
├── Orders/
│   ├── Commands/
│   │   ├── CreateOrderCommand.cs
│   │   ├── CreateOrderHandler.cs
│   │   ├── ConfirmOrderCommand.cs
│   │   └── ConfirmOrderHandler.cs
│   ├── Queries/
│   │   ├── GetOrderQuery.cs
│   │   └── GetOrderHandler.cs
│   └── DTOs/
│       ├── OrderDto.cs
│       └── CreateOrderRequest.cs
└── Validation/
    └── CreateOrderValidator.cs
```

**`IApplicationDbContext` — the key to dropping the repository pattern for reads:**

```csharp
// MyApi.Application/Common/IApplicationDbContext.cs
using MyApi.Domain.Orders;

namespace MyApi.Application.Common;

public interface IApplicationDbContext
{
    DbSet<Order> Orders { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
```

**Command handler (write side of CQRS):**

```csharp
// MyApi.Application/Orders/Commands/CreateOrderHandler.cs
using MyApi.Domain.Common;
using MyApi.Domain.Orders;
using MyApi.Application.Common;

namespace MyApi.Application.Orders.Commands;

public class CreateOrderCommand
{
    public required IReadOnlyList<OrderItem> Items { get; init; }
}

public class CreateOrderHandler
{
    private readonly IOrderRepository _orders;

    public CreateOrderHandler(IOrderRepository orders) => _orders = orders;

    public async Task<Guid> Handle(CreateOrderCommand cmd, CancellationToken ct)
    {
        var order = Order.Create(cmd.Items);   // domain rules enforced here
        await _orders.SaveAsync(order, ct);
        return order.Id;
    }
}
```

**Query handler (read side — uses DbContext directly):**

```csharp
// MyApi.Application/Orders/Queries/GetOrderHandler.cs
namespace MyApi.Application.Orders.Queries;

public record GetOrderQuery(Guid Id);

public class GetOrderHandler
{
    private readonly IApplicationDbContext _db;
    public GetOrderHandler(IApplicationDbContext db) => _db = db;

    public async Task<OrderDto?> Handle(GetOrderQuery q, CancellationToken ct)
    {
        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == q.Id, ct);

        return order is null ? null : new OrderDto(
            order.Id, order.Status, order.Total, order.PlacedAt);
    }
}
```

**FluentValidation in Application:**

```csharp
// MyApi.Application/Validation/CreateOrderValidator.cs
using FluentValidation;
using MyApi.Application.Orders.Commands;

namespace MyApi.Application.Validation;

public class CreateOrderValidator : AbstractValidator<CreateOrderCommand>
{
    public CreateOrderValidator()
    {
        RuleFor(x => x.Items).NotEmpty().WithMessage("At least one item is required.");
        RuleForEach(x => x.Items)
            .ChildRule(item =>
            {
                item.RuleFor(i => i.Quantity).GreaterThan(0);
                item.RuleFor(i => i.Price).GreaterThanOrEqualTo(0);
            });
    }
}
```

---

### Step 5 — Infrastructure Layer (EF Core)

```
MyApi.Infrastructure/
├── Data/
│   ├── ApplicationDbContext.cs
│   └── Configurations/
│       └── OrderConfiguration.cs
├── Repositories/
│   └── OrderRepository.cs
└── DependencyInjection.cs
```

**DbContext implementing the Application interface:**

```csharp
// MyApi.Infrastructure/Data/ApplicationDbContext.cs
using MyApi.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace MyApi.Infrastructure.Data;

public class ApplicationDbContext : DbContext, IApplicationDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options) { }

    public DbSet<Order> Orders => Set<Order>();
}
```

**Repository implementation:**

```csharp
// MyApi.Infrastructure/Repositories/OrderRepository.cs
using MyApi.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace MyApi.Infrastructure.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly ApplicationDbContext _db;
    public OrderRepository(ApplicationDbContext db) => _db = db;

    public async Task<Order?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task SaveAsync(Order order, CancellationToken ct = default)
    {
        _db.Orders.Update(order);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct = default)
        => await _db.Orders.Include(o => o.Items).ToListAsync(ct);
}
```

**DI registration (composition root helper):**

```csharp
// MyApi.Infrastructure/DependencyInjection.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApi.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services,
        string connectionString)
    {
        services.AddDbContext<ApplicationDbContext>(opt =>
            opt.UseNpgsql(connectionString));

        services.AddScoped<IApplicationDbContext>(sp =>
            sp.GetRequiredService<ApplicationDbContext>());

        services.AddScoped<IOrderRepository, OrderRepository>();
        return services;
    }
}
```

---

### Step 6 — API / Presentation Layer (Minimal APIs)

```csharp
// MyApi.Api/Program.cs
using FluentValidation;
using MyApi.Application.Validation;
using MyApi.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure (DbContext, repositories)
builder.Services.AddInfrastructure(
    builder.Configuration.GetConnectionString("Default")!);

// Application (validation, handlers)
builder.Services.AddValidatorsFromAssembly(typeof(CreateOrderValidator).Assembly);

// OpenAPI + Problem Details
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Middleware
app.UseExceptionHandler();
app.UseHttpsRedirection();

// OpenAPI docs
app.MapOpenApi();
app.MapScalarApiReference();

// ── Orders endpoints ──────────────────────────────────────
var api = app.MapGroup("/api/v1");

api.MapGet("/orders/{id:guid}", async (Guid id, GetOrderHandler handler,
    CancellationToken ct) =>
{
    var dto = await handler.Handle(new GetOrderQuery(id), ct);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
})
.WithName("GetOrder")
.Produces<OrderDto>(StatusCodes.Status200OK)
.Produces(StatusCodes.Status404NotFound);

api.MapPost("/orders", async (CreateOrderRequest request,
    IValidator<CreateOrderCommand> validator,
    CreateOrderHandler handler, CancellationToken ct) =>
{
    var cmd = new CreateOrderCommand
    {
        Items = request.Items.Select(i => OrderItem.Create(i.Name, i.Price, i.Quantity)).ToList()
    };

    var result = await validator.ValidateAsync(cmd, ct);
    if (!result.IsValid)
        return Results.ValidationProblem(result.ToDictionary());

    var id = await handler.Handle(cmd, ct);
    return Results.Created($"/api/v1/orders/{id}", null);
})
.WithName("CreateOrder")
.Produces(StatusCodes.Status201Created)
.Produces(StatusCodes.Status400BadRequest);

app.Run();
```

**Global exception handler for DomainException → 400:**

```csharp
// MyApi.Api/Middleware/DomainExceptionMiddleware.cs
using MyApi.Domain.Common;

namespace MyApi.Api.Middleware;

public class DomainExceptionMiddleware
{
    private readonly RequestDelegate _next;
    public DomainExceptionMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await _next(ctx); }
        catch (DomainException ex)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = ex.Message });
        }
    }
}
```

---

### Step 7 — BDD with Reqnroll

**Install Reqnroll in the Acceptance Tests project:**

```bash
cd MyApi.AcceptanceTests
dotnet add package Reqnroll.xUnit
dotnet add package Reqnroll.Tools.MsBuild.Generation
dotnet add package FluentAssertions
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package Microsoft.Extensions.Http
```

**Project structure:**

```
MyApi.AcceptanceTests/
├── Features/
│   └── Order.feature
├── StepDefinitions/
│   └── OrderSteps.cs
├── Support/
│   ├── TestDependencies.cs
│   └── ApiClient.cs
└── reqnroll.json
```

**`reqnroll.json`:**

```json
{
  "$schema": "https://schemas.reqnroll.net/reqnroll-config-latest.json",
  "bindingNamespace": "MyApi.AcceptanceTests.StepDefinitions"
}
```

**Gherkin feature file:**

```gherkin
# MyApi.AcceptanceTests/Features/Order.feature
Feature: Order Management
  As a customer
  I want to create and confirm orders
  So that I can track my purchases

  Scenario: Create a new order with valid items
    Given the API is running
    When I POST /api/v1/orders with 2 items
    Then the response status should be 201
    And the response Location header should contain an order ID

  Scenario: Create an order with no items
    Given the API is running
    When I POST /api/v1/orders with 0 items
    Then the response status should be 400
    And the response body should contain "At least one item"

  Scenario: Confirm a pending order
    Given an order exists with status "Pending"
    When I PUT /api/v1/orders/{id}/confirm
    Then the response status should be 204

  Scenario: Cannot cancel a shipped order
    Given an order exists with status "Shipped"
    When I PUT /api/v1/orders/{id}/cancel
    Then the response status should be 400
```

**Step definitions:**

```csharp
// MyApi.AcceptanceTests/StepDefinitions/OrderSteps.cs
using FluentAssertions;
using Reqnroll;

namespace MyApi.AcceptanceTests.StepDefinitions;

[Binding]
public class OrderSteps
{
    private readonly ScenarioContext _ctx;
    private readonly ApiClient _api;

    public OrderSteps(ScenarioContext ctx, ApiClient api)
    {
        _ctx = ctx;
        _api = api;
    }

    [BeforeScenario]
    public void Setup() => _api.Reset();

    [Given("the API is running")]
    public void GivenTheApiIsRunning()
    {
        // API is started via TestDependencies (WebApplicationFactory)
    }

    [When(@"I POST /api/v1/orders with (\d+) items")]
    public async Task WhenICreateOrderWithItems(int count)
    {
        var items = Enumerable.Range(1, count)
            .Select(i => new { Name = $"Item {i}", Price = 10.0m, Quantity = 1 })
            .ToList();

        _ctx.Set("response",
            await _api.PostAsync("/api/v1/orders", new { Items = items }));
    }

    [Then("the response status should be {int}")]
    public void ThenStatusCode(int expected)
    {
        var resp = _ctx.Get<HttpResponseMessage>("response");
        resp.StatusCode.Should().HaveStatusCode(expected);
    }

    [Then(@"the response body should contain ""(.+)"")]
    public async Task ThenBodyContains(string text)
    {
        var resp = _ctx.Get<HttpResponseMessage>("response");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain(text);
    }
}
```

**DI for tests (using `WebApplicationFactory`):**

```csharp
// MyApi.AcceptanceTests/Support/TestDependencies.cs
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace MyApi.AcceptanceTests.Support;

public static class TestDependencies
{
    [ScenarioDependencies]
    public static IServiceCollection CreateServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<WebApplicationFactory<Program>>();
        services.AddScoped(sp =>
        {
            var factory = sp.GetRequiredService<WebApplicationFactory<Program>>();
            return factory.CreateClient();
        });
        services.AddScoped<ApiClient>();
        return services;
    }
}
```

---

### Step 8 — Unit Tests (Domain + Application)

**Domain tests — pure, no mocks needed:**

```csharp
// MyApi.Domain.Tests/OrderTests.cs
using FluentAssertions;
using MyApi.Domain.Common;
using MyApi.Domain.Orders;

namespace MyApi.Domain.Tests;

public class OrderTests
{
    [Fact]
    public void Create_WithNoItems_Throws()
    {
        Action act = () => Order.Create([]);
        act.Should().Throw<DomainException>()
           .WithMessage("An order must have at least one item.");
    }

    [Fact]
    public void Confirm_FromPending_SetsConfirmed()
    {
        var order = Order.Create([OrderItem.Create("X", 10, 1)]);
        order.Confirm();
        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public void Cancel_FromShipped_Throws()
    {
        var order = Order.Create([OrderItem.Create("X", 10, 1)]);
        // simulate shipped state via reflection or test helper
        // ...
    }
}
```

**Application tests — mock the repository:**

```csharp
// MyApi.Application.Tests/CreateOrderHandlerTests.cs
using NSubstitute;
using MyApi.Application.Orders.Commands;
using MyApi.Domain.Orders;

namespace MyApi.Application.Tests;

public class CreateOrderHandlerTests
{
    private readonly IOrderRepository _repo = Substitute.For<IOrderRepository>();
    private readonly CreateOrderHandler _handler = new(_repo);

    [Fact]
    public async Task Handle_ValidItems_ReturnsNewId()
    {
        var cmd = new CreateOrderCommand
        {
            Items = [OrderItem.Create("A", 5, 2)]
        };

        var id = await _handler.Handle(cmd, CancellationToken.None);
        id.Should().NotBeEmpty();
        await _repo.Received(1).SaveAsync(Arg.Any<Order>(), Arg.Any<CancellationToken>());
    }
}
```

---

### Step 9 — Code Quality Metrics & Enforcement

#### 9a. SonarQube / SonarCloud — Static Analysis

**Add the SonarQube MSBuild integration:**

```bash
dotnet tool install -g dotnet-sonarscanner
```

**`sonar-project.properties` at solution root:**

```properties
sonar.projectKey=MyApi
sonar.sources=.
sonar.exclusions=**/Migrations/**,**/*.Designer.cs,**/obj/**,**/bin/**
sonar.c.file.cyclomaticComplexity.max=10
sonar.c.file.cognitiveComplexity.max=15
sonar.c.maintainability.index.min=65
sonar.coupling.objects.max=5
sonar.inheritance.depth.max=5
sonar.duplicated_lines_density.max=5
sonar.new_code.max=1
```

**CI pipeline step (GitHub Actions example):**

```yaml
- name: SonarQube Scan
  uses: sonarsource/sonarqube-scan-action@v4
  env:
    SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
    SONAR_HOST_URL: ${{ secrets.SONAR_HOST_URL }}
```

**Quality Gate configuration in SonarQube (Project Settings → Quality Gate):**

| Rule | Threshold |
|---|---|
| `Cyclomatic Complexity` | **≤ 10** per method |
| `Cognitive Complexity` | **≤ 15** per method |
| `Maintainability Index` | **≥ 65** |
| `Coupling Between Objects (CBO)` | **≤ 5** |
| `Depth of Inheritance (DIT)` | **≤ 5** |
| `Duplicated Lines` | **≤ 5%** |
| `Technical Debt Ratio` | **≤ 15%** |

> SonarQube natively computes cyclomatic complexity, cognitive complexity, CBO, DIT, LCOM, duplicated lines, and technical debt ratio for C#. Maintainability Index (MI) is available in the C# analyzer. The quality gate **blocks the build** if any threshold is violated.

#### 9b. Stryker.NET — Mutation Testing

**Install:**

```bash
dotnet new tool-manifest
dotnet tool install dotnet-stryker
```

**`stryker-config.json` in the test project:**

```json
{
  "stryker-config": {
    "project": "../MyApi.Application/MyApi.Application.csproj",
    "thresholds": {
      "high": 80,
      "low": 70,
      "break": 70
    },
    "mutate": [
      "**/*.cs",
      "!**/Migrations/**",
      "!**/*.Designer.cs"
    ],
    "reporters": ["html", "progress", "json"]
  }
}
```

**Run:**

```bash
cd MyApi.Application.Tests
dotnet stryker
```

> **Target: > 70% mutation score.** The `break` threshold of 70 means CI fails if the score drops below 70%. This ensures tests are *meaningful*, not just present.

#### 9c. Architecture Tests (ArchUnitNET) — Enforce the Dependency Rule

```csharp
// MyApi.Application.Tests/ArchitectureTests.cs
using ArchUnitNET.Fluent;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace MyApi.Application.Tests;

public class ArchitectureTests
{
    private static readonly IArchRuleDomain Domain =
        Types.InAssemblies(typeof(MyApi.Domain.Common.Entity).Assembly);

    [Fact]
    public void Domain_Should_Not_Reference_Application()
    {
        Domain.Should().NotHaveDependencyOn(typeof(MyApi.Application.Common.IApplicationDbContext).Assembly);
    }

    [Fact]
    public void Application_Should_Not_Reference_Infrastructure()
    {
        Types.InAssemblies(typeof(MyApi.Application.Common.IApplicationDbContext).Assembly)
            .Should().NotHaveDependencyOn(typeof(MyApi.Infrastructure.Data.ApplicationDbContext).Assembly);
    }

    [Fact]
    public void Domain_Should_Not_Reference_EFCore()
    {
        Domain.Should().NotHaveDependencyOn("Microsoft.EntityFrameworkCore");
    }

    [Fact]
    public void Application_Should_Not_Reference_AspNetCore()
    {
        Types.InAssemblies(typeof(MyApi.Application.Common.IApplicationDbContext).Assembly)
            .Should().NotHaveDependencyOn("Microsoft.AspNetCore");
    }
}
```

> These tests **fail the build** if someone accidentally adds a framework reference to the wrong layer — protecting the architecture at CI time, not just at compile time.

#### 9d. Metrics You Track Over Time (CI Dashboard)

| Metric | Tool | Target | How to Measure |
|---|---|---|---|
| **Defect Density** | SonarQube + issue tracker | < 1/KLOC | Confirmed bugs ÷ (LOC/1000) |
| **Mutation Score** | Stryker.NET | > 70% | `dotnet stryker` in CI |
| **Code Churn** | Git (e.g., `git log --numstat`) | Trend | Lines changed per file over 90 days |
| **Change Failure Rate** | Deployment tooling (e.g., GitHub Environments) | Trend down | Failed deploys ÷ total deploys |
| **MTTR** | Incident tracker | Trend down | Time from incident to fix |
| **Code Duplication %** | SonarQube | < 5% | Built-in metric |
| **Dead Code %** | Roslyn analyzers + SonarQube | → 0 | `dotnet format --verify-no-changes` + Sonar |
| **Technical Debt Ratio** | SonarQube | < 15% | `sqale_index` metric |
| **Dependency Depth** | ArchUnitNET / manual | < 3 hops | Architecture tests |
| **Blast Radius** | ArchUnitNET + change impact analysis | Minimize | Count of dependent types per change |

---

### Step 10 — CI/CD Pipeline (GitHub Actions)

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

      - name: Unit Tests
        run: dotnet test MyApi.Domain.Tests --no-build --configuration Release --logger trx

      - name: Application Tests
        run: dotnet test MyApi.Application.Tests --no-build --configuration Release --logger trx

      - name: BDD Acceptance Tests
        run: dotnet test MyApi.AcceptanceTests --no-build --configuration Release --logger trx

      - name: Architecture Tests
        run: dotnet test MyApi.Application.Tests --no-build --filter "FullyQualifiedName~ArchitectureTests"

      - name: Mutation Testing (Stryker.NET)
        run: |
          cd MyApi.Application.Tests
          dotnet stryker --break-at 70

      - name: SonarQube Scan
        uses: sonarsource/sonarqube-scan-action@4
        env:
          SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}
          SONAR_HOST_URL: ${{ secrets.SONAR_HOST_URL }}

      - name: Upload Test Results
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: test-results
          path: '**/*.trx'

      - name: Upload Stryker Report
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: stryker-report
          path: '**/StrykerOutput/**/reports/mutation-report.html'
```

---

### Final Solution Structure

```
MyApi/
├── Directory.Packages.props          # Central Package Management
├── Directory.Build.props             # Shared compiler settings
├── sonar-project.properties          # SonarQube config
├── MyApi.slnx
├── MyApi.Domain/                     # ← Zero dependencies
│   ├── Common/
│   └── Orders/
├── MyApi.Application/                # ← References Domain only
│   ├── Common/
│   ├── Orders/
│   └── Validation/
├── MyApi.Infrastructure/             # ← References Application
│   ├── Data/
│   ├── Repositories/
│   └── DependencyInjection.cs
├── MyApi.Api/                        # ← References Application + Infrastructure
│   ├── Middleware/
│   └── Program.cs
├── MyApi.Domain.Tests/
├── MyApi.Application.Tests/          # Includes ArchitectureTests
├── MyApi.AcceptanceTests/            # Reqnroll BDD
│   ├── Features/
│   ├── StepDefinitions/
│   └── Support/
└── .github/workflows/ci.yml
```

**Key principles that keep every metric in target:**

- **Cyclomatic/Cognitive Complexity < 10/15** → one use case per class, domain rules in entities (not services), no nested conditionals.
- **CBO < 5** → each handler depends on at most 1–2 ports (repository + DbContext).
- **DIT < 5** → flat class hierarchies; `Entity` base is the only inheritance.
- **Mutation Score > 70%** → Stryker.NET gates CI; write tests that assert *behavior*, not just *no exception*.
- **Dependency Depth < 3** → `Api → Application → Domain` is exactly 2 hops.
- **Blast Radius** → changing a domain rule touches only `Domain` + its tests; changing a DB query touches only `Infrastructure`.

