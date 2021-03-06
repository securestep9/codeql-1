using Microsoft.CodeAnalysis;
using System;
using System.Diagnostics.CodeAnalysis;
using Semmle.Extraction.Entities;
using System.Collections.Generic;

namespace Semmle.Extraction.CSharp
{
    /// <summary>
    /// State that needs to be available throughout the extraction process.
    /// There is one Context object per trap output file.
    /// </summary>
    internal class Context : Extraction.Context
    {
        /// <summary>
        /// The program database provided by Roslyn.
        /// There's one per syntax tree, which makes things awkward.
        /// </summary>
        public SemanticModel GetModel(SyntaxNode node)
        {
            // todo: when this context belongs to a SourceScope, the syntax tree can be retrieved from the scope, and
            // the node parameter could be removed. Is there any case when we pass in a node that's not from the current
            // tree?
            if (cachedModel == null || node.SyntaxTree != cachedModel.SyntaxTree)
            {
                cachedModel = Compilation.GetSemanticModel(node.SyntaxTree);
            }

            return cachedModel;
        }

        private SemanticModel cachedModel;

        /// <summary>
        /// The current compilation unit.
        /// </summary>
        public Compilation Compilation { get; }

        internal CommentProcessor CommentGenerator { get; } = new CommentProcessor();

        public Context(Extraction.Extractor e, Compilation c, TrapWriter trapWriter, IExtractionScope scope, bool addAssemblyTrapPrefix)
            : base(e, trapWriter, addAssemblyTrapPrefix)
        {
            Compilation = c;
            this.scope = scope;
        }

        public bool FromSource => scope is SourceScope;

        private readonly IExtractionScope scope;

        public bool IsAssemblyScope => scope is AssemblyScope;

        private SyntaxTree SourceTree => scope is SourceScope sc ? sc.SourceTree : null;

        /// <summary>
        ///     Whether the given symbol needs to be defined in this context.
        ///     This is the case if the symbol is contained in the source/assembly, or
        ///     of the symbol is a constructed generic.
        /// </summary>
        /// <param name="symbol">The symbol to populate.</param>
        public bool Defines(ISymbol symbol) =>
            !SymbolEqualityComparer.Default.Equals(symbol, symbol.OriginalDefinition) ||
            scope.InScope(symbol);

        public override void WithDuplicationGuard(Key key, Action a)
        {
            if (IsAssemblyScope)
            {
                // No need for a duplication guard when extracting assemblies,
                // and the duplication guard could lead to method bodies being missed
                // depending on trap import order.
                a();
            }
            else
            {
                base.WithDuplicationGuard(key, a);
            }
        }

        public override Extraction.Entities.Location CreateLocation()
        {
            return SourceTree == null
                ? GeneratedLocation.Create(this)
                : CreateLocation(Microsoft.CodeAnalysis.Location.Create(SourceTree, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(0, 0)));
        }

        public override Extraction.Entities.Location CreateLocation(Microsoft.CodeAnalysis.Location location)
        {
            return (location == null || location.Kind == LocationKind.None)
                ? GeneratedLocation.Create(this)
                : location.IsInSource
                    ? Entities.NonGeneratedSourceLocation.Create(this, location)
                    : Entities.Assembly.Create(this, location);
        }

        /// <summary>
        /// Register a program entity which can be bound to comments.
        /// </summary>
        /// <param name="cx">Extractor context.</param>
        /// <param name="entity">Program entity.</param>
        /// <param name="l">Location of the entity.</param>
        public void BindComments(Entity entity, Microsoft.CodeAnalysis.Location l)
        {
            var duplicationGuardKey = GetCurrentTagStackKey();
            CommentGenerator.AddElement(entity.Label, duplicationGuardKey, l);
        }

        protected override bool IsEntityDuplicationGuarded(IEntity entity, [NotNullWhen(true)] out Extraction.Entities.Location loc)
        {
            if (CreateLocation(entity.ReportingLocation) is Entities.NonGeneratedSourceLocation l)
            {
                loc = l;
                return true;
            }

            loc = null;
            return false;
        }

        private readonly HashSet<Label> extractedGenerics = new HashSet<Label>();

        /// <summary>
        /// Should the given entity be extracted?
        /// A second call to this method will always return false,
        /// on the assumption that it would have been extracted on the first call.
        ///
        /// This is used to track the extraction of generics, which cannot be extracted
        /// in a top-down manner.
        /// </summary>
        /// <param name="entity">The entity to extract.</param>
        /// <returns>True only on the first call for a particular entity.</returns>
        internal bool ExtractGenerics(CachedEntity entity)
        {
            if (extractedGenerics.Contains(entity.Label))
            {
                return false;
            }

            extractedGenerics.Add(entity.Label);
            return true;
        }
    }
}
