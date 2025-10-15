using EquipmentLendingApi.Dtos;
using FluentValidation;

namespace EquipmentLendingApi.Validators
{
    public class RequestDtoValidator : AbstractValidator<RequestDto>
    {
        public RequestDtoValidator()
        {
            RuleFor(x => x.EquipmentId)
                .GreaterThan(0).WithMessage("Equipment ID is required");

            RuleFor(x => x.StartDate)
                .NotEmpty().WithMessage("Start date is required")
                .GreaterThanOrEqualTo(DateTime.Today).WithMessage("Start date cannot be in the past");

            RuleFor(x => x.EndDate)
                .NotEmpty().WithMessage("End date is required")
                .GreaterThan(x => x.StartDate).WithMessage("End date must be after start date");

            RuleFor(x => x)
                .Must(x => (x.EndDate - x.StartDate).TotalDays <= 30)
                .WithMessage("Lending period cannot exceed 30 days");
        }
    }
}
