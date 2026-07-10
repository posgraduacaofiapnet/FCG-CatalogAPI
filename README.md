# FCG-CatalogAPI

Microservice responsible for the game catalog, purchase flow initiation, and library management. Publishes `OrderPlacedEvent` and consumes `PaymentProcessedEvent` to update the user's library after payment approval.

Part of **FIAP Cloud Games (FCG)** — Tech Challenge Phase 2.

## Tech Stack

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 + SQL Server
- MassTransit + RabbitMQ
- JWT Bearer Authentication
- Swagger / OpenAPI

## Endpoints

| Method | Route | Description | Auth |
|--------|-------|-------------|------|
| `GET` | `/api/games` | List all games | No |
| `GET` | `/api/games/{id}` | Get game by ID | No |
| `POST` | `/api/games` | Create a new game | Yes |
| `PUT` | `/api/games/{id}` | Update a game | Yes |
| `DELETE` | `/api/games/{id}` | Deactivate a game (soft delete) | Yes |
| `POST` | `/api/library/purchase` | Purchase a game | Yes (must be the owner) |
| `GET` | `/api/library/{userId}` | Get user's game library | Yes (must be the owner) |
| `GET` | `/health` | Health check | No |

Auth uses a JWT Bearer token issued by **FCG-UsersAPI** (`POST /api/auth/login`). Endpoints marked "must be the owner" compare the token's `user_id` claim against the `userId` in the request — a token can only purchase or list a library for its own user, returning `403 Forbidden` otherwise.

### Create Game payload

```json
{
  "title": "Cyber FIAP",
  "description": "Demo game for purchase flow.",
  "price": 99.90
}
```

### Update Game payload

```json
{
  "title": "Cyber FIAP - Remastered",
  "description": "Updated demo game for purchase flow.",
  "price": 79.90
}
```

### Purchase payload

```json
{
  "userId": "<user-guid>",
  "gameId": "<game-guid>"
}
```

## Events

| Direction | Event | Trigger |
|-----------|-------|---------|
| Publishes | `OrderPlacedEvent` | After a purchase is requested |
| Consumes | `PaymentProcessedEvent` | Adds game to library when payment is approved |

## Event-Driven Purchase Flow

```
User → POST /api/library/purchase
  → CatalogAPI publishes OrderPlacedEvent
    → PaymentsAPI processes and publishes PaymentProcessedEvent
      → CatalogAPI adds game to user library
      → NotificationsAPI sends confirmation email
```

## Environment Variables

| Variable | Description |
|----------|-------------|
| `ConnectionStrings__DefaultConnection` | SQL Server connection string |
| `Jwt__Key` | HMAC signing key for validating tokens — must match FCG-UsersAPI's `Jwt__Key` |
| `Jwt__Issuer` | Expected token issuer — must match FCG-UsersAPI's `Jwt__Issuer` |
| `Jwt__Audience` | Expected token audience — must match FCG-UsersAPI's `Jwt__Audience` |
| `RabbitMq__Host` | RabbitMQ hostname |
| `RabbitMq__Username` | RabbitMQ username |
| `RabbitMq__Password` | RabbitMQ password |
| `RabbitMq__PaymentProcessedQueue` | Queue name for payment results |

## Running Locally

### Docker Compose (via FCG-Orchestration)

```bash
cd FCG-Orchestration
docker compose up --build
```

Swagger: http://localhost:5102/swagger

### Kubernetes

```bash
# Build the image first
cd FCG-CatalogAPI
docker build -t fcg-catalog-api:latest -f services/CatalogAPI/Dockerfile .

# Apply manifests
cd k8s
kubectl apply -f .

# Verify
kubectl get pods
kubectl port-forward service/catalog-api 5102:80
```

Swagger: http://localhost:5102/swagger

## Solution Structure

```
FCG-CatalogAPI/
├── FCG-CatalogAPI.sln
├── contracts/
│   └── FCG.Contracts/        # Shared event contracts
├── services/
│   └── CatalogAPI/           # Main service project
└── k8s/                      # Kubernetes manifests
    ├── deployment.yaml
    ├── service.yaml
    ├── configmap.yaml
    └── secret.yaml
```

## Related Repositories

- [FCG-Orchestration](https://github.com/posgraduacaofiapnet/FCG-Orchestration) — Docker Compose + global K8s infra
- [FCG-UsersAPI](https://github.com/posgraduacaofiapnet/FCG-UsersAPI)
- [FCG-PaymentsAPI](https://github.com/posgraduacaofiapnet/FCG-PaymentsAPI)
- [FCG-NotificationsAPI](https://github.com/posgraduacaofiapnet/FCG-NotificationsAPI)
