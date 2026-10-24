# C# Style: Pragmatic, Not Enterprise

Write code the way an experienced developer would write it by hand: direct, readable, few types.
Fewer classes is the default. Every new type must earn its place.

## Write less code
- Make the smallest change that fully solves the request. If 200 lines were written and 50 would do, rewrite it as 50.
- No features, options, config or flexibility that weren't asked for.
- Before writing new code, check in this order: existing code in this repo → .NET and the frameworks the project
  already uses → a package already referenced → only then new code. No new NuGet or npm package without asking first.
- Find the closest existing code that does something similar and match its shape. Don't invent a new pattern.
- No caching, memoization, batching, parallelism, retries, queues or pooling without a named performance need. Match
  the design to the real load: 50 rows a day doesn't need it. Retries an existing integration already has stay.
- Load what a method needs in a few queries before the loop, then work in memory. No query inside a loop over rows.
- No overloads, optional parameters, `[Obsolete]` paths or compatibility shims for a caller that doesn't exist. Change
  the call sites instead.
- Prefer editing an existing file to adding one. A new file needs a reason tied to the request.
- Would an experienced developer reviewing this call it overcomplicated? If so, simplify before finishing.

## Method shape
- A long method is fine if it reads top to bottom as one flow; mark steps with a short comment.
- A step that is long or repeated inside the method becomes a local function after the `return`, using the
  method's variables instead of taking parameters. A private method is for code that two or more methods use.
- No helper, utility or extension methods for code used in one place. Extract to a class or shared method only on
  reuse (rule of three) or when it is a real business rule that gets a test.
- Flat over nested: early returns, no deep if/else chains.
- A type used by only one class stays private, nested in it (e.g. the JSON shape of an external API). Intermediate
  data inside a method uses anonymous types or tuples, not a declared class. API DTOs still go one per file in Shared.

## Errors
- No error handling for states that can't happen: null checks on values that are never null, try/catch around code
  that doesn't throw, validating again what the caller already validated.
- Broken data or config: throw at the point of use with a message that says what is wrong (using the repo's
  exception types). Don't add recovery, defaults or retries for it.
- Input validation, auth, tenant and other security checks are never "can't happen". Cut scope, not correctness.

## Comments
- Comment the business reason for a branch ("the initiator can cancel the process") and constraints the code can't
  show (a required unique index, an order that matters), next to the code they apply to.
- No comments that repeat the code, narrate steps ("// load the order") or describe the change ("// added for X").
- No commented-out code, no author/date headers (git has them).

## Changing existing code
- Stay surgical: every changed line traces to the request. Don't refactor, rename, reformat or tidy adjacent code
  that isn't broken; mention it in the summary instead.
- When fixing a bug, change only the lines that cause it, and add a test that reproduces it.
- Remove usings, variables and methods your change made unused. Deleting code your change made obsolete is
  preferred. Report unrelated dead code; don't delete it unless asked.
- If the request is unclear in a way that changes what gets built, ask before building, not after.

## Solution layout
Fixed layout; `AppName` is the product name. Don't add other projects (Domain/Application/Infrastructure) without asking.
- `AppName.Shared`: dependency-free code that every other project may reference. DTO records in
  `Types/<Module>/` (one record per file), plus shared enums, constants, exceptions, boundary interfaces
  (e.g. `IEmailReader`) and small utility classes. No EF, no ASP.NET, no business logic, no references to other projects.
- `AppName.Core`: everything except HTTP. Entities, `DbContext`, migrations, repositories,
  services, business rules. Feature folders inside (e.g. `Portfolio/`, `Potentials/`).
  References Shared. Exposes one `AddCore(this IServiceCollection, IConfiguration)` extension for DI registration.
- `AppName.Integrations`: implementations of the boundary interfaces for external systems (mail providers,
  payment gateway, third-party APIs). One folder per system. References Shared only (not Core). Core or the Web project registers them in DI.
- `AppName.Web`: ASP.NET Core MVC (Razor views + SignalR hub + background workers) with regular controllers. References Core.
  Contains controllers, views, wwwroot, hubs, workers, `Program.cs`, middleware and config only. Workers and hubs are thin like controllers: they call Core services.
- `AppName.Tests`: unit and integration tests.

## Controllers
- Controllers are thin, with no business logic and no `DbContext` access. Each action calls one
  service method and returns the result.
- Request/response DTOs are the records from Shared. Don't create a second set of API models
  and map between them.
- Turn errors into HTTP responses in one place (exception-handling middleware or
  `IExceptionHandler`), not with try/catch in every action.
- Don't pass a `CancellationToken` from controllers into services, repositories or `DbContext` calls unless there is a
  real need (a long-running or streaming call, e.g. the agent turn stream). Background services and workers must
  accept and honour cancellation (review them for it).

Example:
    [ApiController]
    [Route("api/orders")]
    public class OrdersController(OrderService orders) : ControllerBase
    {
        [HttpPost("{id}/cancel")]
        public async Task<IActionResult> Cancel(int id, CancellationToken ct)
        {
            await orders.Cancel(id, ct);
            return NoContent();
        }
    }

## Abstractions
- Don't add an interface for a class with one implementation. Exceptions:
  - Repositories (see Data access) may have one interface each, for mocking.
  - Boundaries to the outside world that tests must fake (email, payment gateway, third-party
    HTTP API, file storage): one interface per external system, not one per class.
- Services are concrete classes with no interface. Controllers inject them directly.
- Don't wrap framework types. Use `DbContext`, `HttpClient`, `ILogger<T>`, `IOptions<T>` and
  `TimeProvider` directly. No unit-of-work wrapper, no `IDateTimeProvider` or `IClock`.
- Don't create Factory, Builder, Manager, Helper, Provider, Wrapper or Base* classes unless there
  is real logic to hold. A class with one method that forwards a call should not exist.
- Rule of three: duplicate code twice, abstract on the third occurrence.
- No inheritance for code reuse. Use plain methods or composition.
- Don't add these libraries unless I ask: MediatR, AutoMapper, FluentValidation, Specification
  pattern, generic Result/Either types, CQRS frameworks.

## Data access
- One repository per aggregate root, not per table: `OrderRepository` covers Order + OrderDetails.
  Child entities never get their own repository.
- Repositories are READ-ONLY: they load data and never save.
  - `GetById` returns the tracked root entity, with Includes only for essential related data
    that almost every caller needs.
  - For other related data, add separate named methods, e.g. `GetByIdWithDetails`,
    `GetByIdWithPayments`. Add them only when a caller actually needs them.
  - A service must load through a method that includes everything it will read or modify.
    Never rely on lazy loading.
  - List/lookup methods return `List<T>`/`IEnumerable<T>` of entities or DTOs. Use `AsNoTracking`
    for display-only reads.
  - Every method is a specific, named query that's actually used. No generic `IRepository<T>`,
    no `Find(Expression<...>)`, no returning `IQueryable`.
- One interface per repository, with one implementation, in the same file.
- Writes happen in the service, using `DbContext` directly:
  - Update: `var order = await orderRepo.GetById(id)` → modify → `await db.SaveChangesAsync()`
  - Insert: `db.Orders.Add(order)` → `await db.SaveChangesAsync()`
  - Delete: `db.Orders.Remove(order)` → `await db.SaveChangesAsync()`
- No Add/Update/Delete/Save methods on repositories.
- A query used in only one place may stay inline in the service. Move it to the repository when a
  second caller needs it, or when it's an aggregate load (`GetById*`).

Example:
    public interface IOrderRepository
    {
        Task<Order?> GetById(int id, CancellationToken ct);
        Task<Order?> GetByIdWithDetails(int id, CancellationToken ct);
        Task<List<OrderSummary>> ListForCustomer(int customerId, CancellationToken ct);
    }

    public class OrderRepository(AppDbContext db) : IOrderRepository
    {
        public async Task<Order?> GetById(int id, CancellationToken ct)
        {
            return await db.Orders.FirstOrDefaultAsync(o => o.Id == id, ct);
        }

        public async Task<Order?> GetByIdWithDetails(int id, CancellationToken ct)
        {
            return await db.Orders.Include(o => o.Details).FirstOrDefaultAsync(o => o.Id == id, ct);
        }

        public async Task<List<OrderSummary>> ListForCustomer(int customerId, CancellationToken ct)
        {
            return await db.Orders.AsNoTracking()
                .Where(o => o.CustomerId == customerId)
                .Select(o => new OrderSummary(o.Id, o.Total, o.Status))
                .ToListAsync(ct);
        }
    }

    public class OrderService(AppDbContext db, IOrderRepository orders)
    {
        public async Task Cancel(int id, CancellationToken ct)
        {
            var order = await orders.GetById(id, ct) ?? throw new NotFoundException();
            OrderRules.EnsureCancellable(order);
            order.Status = OrderStatus.Cancelled;
            await db.SaveChangesAsync(ct);
        }
    }

## Structure (inside Core)
- Organize by feature, not by layer. A feature's service and repository live together
  in one feature folder (e.g. `Core/Modules/Orders/`). Split a file only when it passes about 300 lines.
- Entities live in `Core/Model/<Entity>/` (one file per entity, plus `Model/AppDbContext.cs` and `Model/Migrations/`).
- A feature's service and repository (reads) live in `Core/Modules/<Feature>/`; its DTOs go in
  `Shared/Types/<Feature>/`, one record per file.
- DTO records always have the `Dto` suffix (`CampaignSummaryDto`, `CreateCampaignDto`, `PagedResultDto<T>`).
- Mapping is a few lines of code or a static method. No mapping classes.
- Validation goes inline at the top of the service method. Return early.

## Testability
- Put business rules in pure `static` methods or entity methods (input → output, no I/O) and unit
  test those directly, without a DB or mocks.
- Services: mock the repository to control what gets loaded. Use a real `DbContext`
  (SQLite in-memory) for `SaveChanges`. Don't mock `DbContext` or `DbSet`.
- Repositories: test against a real database (SQLite in-memory or Testcontainers).
- Controllers have no logic, so no unit tests for them. Cover the API with a few
  `WebApplicationFactory` integration tests if needed.
- Time: inject `TimeProvider` (use `FakeTimeProvider` in tests).
- HTTP: use a fake `HttpMessageHandler`. Don't put an interface around the client.
- If a test needs a mock of something that's neither a repository nor an external boundary,
  restructure the code instead of adding an interface.

## Modern C#
- File-scoped namespaces, primary constructors, `record` for DTOs, collection expressions,
  nullable enabled.
- No expression-bodied methods (`=>`): write methods with a normal block body, even for one line
  (`{ return ...; }` or `{ await ...; }`). Lambdas are fine.

## Naming and clarity
- Name a thing for what it is or does, not for its role in your head. No `fallback`, `Slot`, `ChoiceOf`, `Data`,
  `Info`. `defaultClientFromConfig`, `ModelChoice`, `ModelChoiceOrNull` say it. If a name needs a comment to be
  understood, rename it.
- One word, one meaning. Don't use a word for two different things (a settings entry and a private record both
  called "slot"). Two types that hold the same idea: use one (a private record that repeats a DTO is not needed;
  use the DTO).
- A method that can return null says so (`...OrNull`); a conversion says what it converts to.
- An interface method that this class must never be called through throws `NotSupportedException` with a message
  that names the right entry point. Don't forward it to something plausible: that hides callers that bypass the
  intended path.
- Don't build a setting, option or UI control that no code uses yet. If the plan lists one, check first whether
  anything uses it, and say so before building it, not after.
- When a plan or request says to do something "like X" (e.g. "encrypt like the mail passwords") check what X
  really does. If it differs, say so and follow the repo (see Consistency).
- After a review fix, add a test for the unhappy path it exposed: a rule that must hold for the effective value
  (not only the field), a secret that must not go to a user-supplied address, a failure that must not be remembered.

## Before you finish
Read your diff and:
- revert edits to files outside the task;
- inline abstractions, helpers and private methods with one caller;
- remove try/catch and guards around code that can't fail;
- remove comments that narrate or repeat the code;
- remove anything that doesn't change behavior or trace to the request.

List every new type (class/interface/record) you created, with one line saying why it exists.
If the reason is "separation of concerns", "future flexibility" or "best practice", delete it
and inline the code.

## Consistency
If existing code in the repo already follows a convention, follow it, even if it conflicts with
the rules above. Point out the conflict instead of refactoring it on your own.
