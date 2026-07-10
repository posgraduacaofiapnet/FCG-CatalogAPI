# CatalogAPI

Microsservico responsavel pelo catalogo de jogos, inicio do fluxo de compra e atualizacao da biblioteca apos pagamento aprovado.

## Endpoints

- `GET /api/games`
- `GET /api/games/{id}`
- `POST /api/games` (JWT obrigatorio)
- `PUT /api/games/{id}` (JWT obrigatorio)
- `DELETE /api/games/{id}` (JWT obrigatorio, soft delete via IsActive)
- `POST /api/library/purchase` (JWT obrigatorio, `userId` do corpo deve ser o dono do token)
- `GET /api/library/{userId}` (JWT obrigatorio, so o proprio dono)
- `GET /health`

## Eventos

- Publica: `OrderPlacedEvent`
- Consome: `PaymentProcessedEvent`

## Variaveis

- `ConnectionStrings__DefaultConnection`
- `Jwt__Key` (mesmo valor do FCG-UsersAPI)
- `Jwt__Issuer` (mesmo valor do FCG-UsersAPI)
- `Jwt__Audience` (mesmo valor do FCG-UsersAPI)
- `RabbitMq__Host`
- `RabbitMq__Username`
- `RabbitMq__Password`
- `RabbitMq__PaymentProcessedQueue`

Esta pasta pode ser movida para um repositorio Git proprio.
