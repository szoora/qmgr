using FluentValidation;
using QMgr.Application.Commands.Queue;

namespace QMgr.Application.Validators;

public class CreateTokenCommandValidator : AbstractValidator<CreateTokenCommand>
{
    public CreateTokenCommandValidator()
    {
        RuleFor(x => x.BranchId)
            .NotEmpty()
            .WithMessage("Branch ID is required.");

        RuleFor(x => x.ServiceTypeCode)
            .NotEmpty()
            .WithMessage("Service type code is required.")
            .MaximumLength(10)
            .WithMessage("Service type code must not exceed 10 characters.");

        When(x => x.Customer != null, () =>
        {
            RuleFor(x => x.Customer!.Phone)
                .Matches(@"^\+?[1-9]\d{1,14}$")
                .When(x => !string.IsNullOrEmpty(x.Customer?.Phone))
                .WithMessage("Invalid phone number format.");

            RuleFor(x => x.Customer!.Email)
                .EmailAddress()
                .When(x => !string.IsNullOrEmpty(x.Customer?.Email))
                .WithMessage("Invalid email format.");

            RuleFor(x => x.Customer!.Name)
                .MaximumLength(255)
                .When(x => !string.IsNullOrEmpty(x.Customer?.Name))
                .WithMessage("Customer name must not exceed 255 characters.");
        });

        RuleFor(x => x.ExternalReference)
            .MaximumLength(100)
            .When(x => !string.IsNullOrEmpty(x.ExternalReference))
            .WithMessage("External reference must not exceed 100 characters.");

        RuleFor(x => x.ExternalSystem)
            .MaximumLength(100)
            .When(x => !string.IsNullOrEmpty(x.ExternalSystem))
            .WithMessage("External system must not exceed 100 characters.");
    }
}
