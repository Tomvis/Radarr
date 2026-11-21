using System.Linq;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Dubcheck;
using NzbDrone.Core.Languages;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.CustomFormats
{
    // Tomvis (MEDIA-39): matches when the dubcheck sidecar says a release has (or lacks) embedded
    // audio in a language. Never matches files, history or blocklist items (no release to check).
    public class DubcheckSpecificationValidator : AbstractValidator<DubcheckSpecification>
    {
        public DubcheckSpecificationValidator()
        {
            RuleFor(c => c.Value).Must(v => Language.All.Any(o => o.Id == v && o != Language.Unknown && o != Language.Original && o != Language.Any))
                .WithMessage("Dubcheck needs a specific language");
            RuleFor(c => c.Verdict).InclusiveBetween((int)DubVerdict.Has, (int)DubVerdict.Lacks);
        }
    }

    public class DubcheckSpecification : CustomFormatSpecificationBase
    {
        private static readonly DubcheckSpecificationValidator Validator = new DubcheckSpecificationValidator();

        // Set by DubcheckClient at startup: specifications are deserialized, not resolved from the container.
        public static IDubcheckClient Client { get; set; }

        public override int Order => 12;
        public override string ImplementationName => "Dubcheck";

        [FieldDefinition(0, Label = "Language", Type = FieldType.Select, SelectOptions = typeof(LanguageFieldConverter))]
        public int Value { get; set; }

        [FieldDefinition(1, Label = "Verdict", Type = FieldType.Select, SelectOptions = typeof(DubcheckVerdictOption), HelpText = "Has: dubcheck found embedded audio in the language. Lacks: dubcheck proved it absent.")]
        public int Verdict { get; set; }

        protected override bool IsSatisfiedByWithoutNegate(CustomFormatInput input)
        {
            if (input.Release == null || input.Movie == null || Client == null)
            {
                return false;
            }

            return (int)Client.Check(input.Release, input.Movie, (Language)Value) == Verdict;
        }

        public override NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
