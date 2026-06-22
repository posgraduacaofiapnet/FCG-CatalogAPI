using FluentValidation;

namespace CatalogAPI;

public sealed class CreateGameRequestValidator : AbstractValidator<CreateGameRequest>
{
    public CreateGameRequestValidator()
    {
        RuleFor(request => request.Title).NotEmpty().MaximumLength(150);
        RuleFor(request => request.Description).NotEmpty().MaximumLength(500);
        RuleFor(request => request.Price).GreaterThan(0);
    }
}

public sealed class PurchaseGameRequestValidator : AbstractValidator<PurchaseGameRequest>
{
    public PurchaseGameRequestValidator()
    {
        RuleFor(request => request.UserId).NotEmpty();
        RuleFor(request => request.GameId).NotEmpty();
    }
}
