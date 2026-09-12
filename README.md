# FCG-CatalogAPI

Microserviço responsável pelo catálogo de jogos, início do fluxo de compra e gerenciamento da biblioteca do usuário. Publica `OrderPlacedEvent` e consome `PaymentProcessedEvent` para adicionar o jogo à biblioteca após aprovação do pagamento.

Parte do **FIAP Cloud Games (FCG)** — Tech Challenge Fase 2.

---

## Tecnologias

- .NET 10 / ASP.NET Core
- Entity Framework Core 10 + SQL Server
- MassTransit + RabbitMQ
- JWT Bearer Authentication
- Swagger / OpenAPI
- Serilog (logs estruturados em JSON)

---

## Endpoints

| Método | Rota | Descrição | Auth |
|--------|------|-----------|------|
| `GET` | `/api/games` | Lista todos os jogos | Não |
| `GET` | `/api/games/{id}` | Busca jogo por ID | Não |
| `POST` | `/api/games` | Cria um novo jogo | Sim |
| `PUT` | `/api/games/{id}` | Atualiza um jogo | Sim |
| `DELETE` | `/api/games/{id}` | Desativa um jogo (soft delete) | Sim |
| `POST` | `/api/library/purchase` | Solicita a compra de um jogo | Sim (dono) |
| `GET` | `/api/library/{userId}` | Retorna a biblioteca do usuário | Sim (dono) |
| `GET` | `/health` | Health check | Não |

A autenticação usa token JWT Bearer emitido pela **FCG-UsersAPI** (`POST /api/auth/login`). Os endpoints marcados como **(dono)** comparam o claim `user_id` do token com o `userId` da requisição — um token só pode comprar ou consultar a biblioteca do seu próprio usuário, retornando `403 Forbidden` caso contrário.

### Payload: Criar Jogo

```json
{
  "title": "Cyber FIAP",
  "description": "Jogo demo para o fluxo de compra.",
  "price": 99.90
}
```

### Payload: Atualizar Jogo

```json
{
  "title": "Cyber FIAP - Remasterizado",
  "description": "Jogo demo atualizado.",
  "price": 79.90
}
```

### Payload: Comprar Jogo

```json
{
  "userId": "<guid-do-usuario>",
  "gameId": "<guid-do-jogo>"
}
```

---

## Eventos

| Direção | Evento | Gatilho |
|---------|--------|---------|
| Publica | `OrderPlacedEvent` | Após `POST /api/library/purchase` (RabbitMQ → PaymentsAPI) |
| Publica | `OrderPaid` | Após a mesma compra, na fila SQS `fcg-notifications-queue` (Lambda de e-mail) |
| Consome | `PaymentProcessedEvent` | Adiciona jogo à biblioteca quando pagamento é aprovado |

---

## Fluxo de Compra (Event-Driven)

```
Usuário → POST /api/library/purchase   (nao existe POST /api/orders)
  → CatalogAPI cria o pedido e responde 202 { id, status }
  → CatalogAPI publica OrderPlacedEvent (RabbitMQ)
  → CatalogAPI publica OrderPaid (SQS fcg-notifications-queue)
      → Lambda fcg-notifications-function registra notification_sent (e-mail simulado)
    → PaymentsAPI consome e simula o processamento
      → PaymentsAPI publica PaymentProcessedEvent (Aprovado | Rejeitado)
        → CatalogAPI consome:
            se Aprovado → adiciona jogo à biblioteca do usuário
        → NotificationsAPI consome:
            se Aprovado → loga e-mail de confirmação de compra (container)
```

---

## Variáveis de Ambiente

| Variável | Descrição |
|----------|-----------|
| `ConnectionStrings__DefaultConnection` | String de conexão do SQL Server |
| `Jwt__Key` | Chave HMAC para validação dos tokens — deve ser idêntica à da FCG-UsersAPI |
| `Jwt__Issuer` | Emissor esperado do token — deve ser idêntico ao da FCG-UsersAPI |
| `Jwt__Audience` | Audiência esperada do token — deve ser idêntica à da FCG-UsersAPI |
| `RabbitMq__Host` | Hostname do RabbitMQ |
| `RabbitMq__Username` | Usuário do RabbitMQ |
| `RabbitMq__Password` | Senha do RabbitMQ |
| `RabbitMq__PaymentProcessedQueue` | Nome da fila para resultados de pagamento |
| `Sqs__NotificationsQueueUrl` | URL da fila SQS `fcg-notifications-queue` |
| `Sqs__Region` | Região AWS da fila (ex: `us-east-1`) |
| `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` | Credenciais para a CatalogAPI publicar na SQS |

---

## Executando Localmente

### Docker Compose (via FCG-Orchestration)

```bash
cd FCG-Orchestration
docker compose up --build
```

Swagger disponível em: http://localhost:5102/swagger

### Kubernetes

```bash
# 1. Build da imagem local
cd FCG-CatalogAPI
docker build -t fcg-catalog-api:latest -f services/CatalogAPI/Dockerfile .

# 2. Aplique a infra (RabbitMQ + SQL Server) primeiro
cd ../FCG-Orchestration/k8s
kubectl apply -f .

# 3. Aplique os manifestos da CatalogAPI
cd ../../FCG-CatalogAPI/k8s
kubectl apply -f .

# 4. Verifique os pods
kubectl get pods
kubectl get services

# 5. Acesse via port-forward
kubectl port-forward service/catalog-api 5102:80
```

Swagger disponível em: http://localhost:5102/swagger

#### Manifestos Kubernetes

| Arquivo | Tipo | Descrição |
|---------|------|-----------|
| `deployment.yaml` | Deployment | Define o Pod com 1 réplica, imagem, probes e referências a ConfigMap/Secret |
| `service.yaml` | Service | Expõe a API internamente no cluster na porta 80 |
| `configmap.yaml` | ConfigMap | Configurações não-sensíveis (RabbitMQ host/username, fila, Jwt Issuer/Audience) |
| `secret.yaml` | Secret | Dados sensíveis em base64 (connection string, Jwt Key, RabbitMQ password) |

As **readinessProbe** e **livenessProbe** do Deployment apontam para `/health` — o pod só recebe tráfego após o healthcheck passar.

---

## Testes Unitários

```bash
cd FCG-CatalogAPI
dotnet test FCG-CatalogAPI.sln
```

Os testes utilizam **xUnit**, **Bogus** para geração de dados fictícios e o provider **InMemory** do Entity Framework Core para isolar a camada de persistência sem banco real.

---

## Estrutura da Solution

```
FCG-CatalogAPI/
├── FCG-CatalogAPI.sln
├── contracts/
│   └── FCG.Contracts/        # Contratos de eventos compartilhados
├── services/
│   └── CatalogAPI/           # Projeto principal do serviço
├── tests/
│   └── CatalogAPI.Tests/     # Testes unitários (xUnit)
└── k8s/                      # Manifestos Kubernetes
    ├── deployment.yaml
    ├── service.yaml
    ├── configmap.yaml
    └── secret.yaml
```

---

## Repositórios Relacionados

- [FCG-Orchestration](https://github.com/posgraduacaofiapnet/FCG-Orchestration) — Docker Compose + infraestrutura K8s global
- [FCG-UsersAPI](https://github.com/posgraduacaofiapnet/FCG-UsersAPI)
- [FCG-PaymentsAPI](https://github.com/posgraduacaofiapnet/FCG-PaymentsAPI)
- [FCG-NotificationsAPI](https://github.com/posgraduacaofiapnet/FCG-NotificationsAPI)
