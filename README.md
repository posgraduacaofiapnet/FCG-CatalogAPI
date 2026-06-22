# CatalogAPI

Microsservico responsavel pelo catalogo de jogos, inicio do fluxo de compra e atualizacao da biblioteca apos pagamento aprovado.

## Endpoints

- `GET /api/games`
- `GET /api/games/{id}`
- `POST /api/games`
- `POST /api/library/purchase`
- `GET /api/library/{userId}`
- `GET /health`

## Eventos

- Publica: `OrderPlacedEvent`
- Consome: `PaymentProcessedEvent`

## Variaveis

- `ConnectionStrings__DefaultConnection`
- `RabbitMq__Host`
- `RabbitMq__Username`
- `RabbitMq__Password`
- `RabbitMq__PaymentProcessedQueue`

Esta pasta pode ser movida para um repositorio Git proprio.
