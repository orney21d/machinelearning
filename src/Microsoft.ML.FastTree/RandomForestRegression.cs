// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Float = System.Single;

using System;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.FastTree;
using Microsoft.ML.Runtime.FastTree.Internal;
using Microsoft.ML.Runtime.Internal.Utilities;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Runtime.Training;
using Microsoft.ML.Runtime.Internal.Internallearn;

[assembly: LoadableClass(FastForestRegression.Summary, typeof(FastForestRegression), typeof(FastForestRegression.Arguments),
    new[] { typeof(SignatureRegressorTrainer), typeof(SignatureTrainer), typeof(SignatureTreeEnsembleTrainer), typeof(SignatureFeatureScorerTrainer) },
    FastForestRegression.UserNameValue,
    FastForestRegression.LoadNameValue,
    FastForestRegression.ShortName)]

[assembly: LoadableClass(typeof(FastForestRegressionPredictor), null, typeof(SignatureLoadModel),
    "FastForest Regression Executor",
    FastForestRegressionPredictor.LoaderSignature)]

namespace Microsoft.ML.Runtime.FastTree
{
    public sealed class FastForestRegressionPredictor :
        FastTreePredictionWrapper,
        IQuantileValueMapper,
        IQuantileRegressionPredictor
    {
        private readonly int _quantileSampleCount;

        public const string LoaderSignature = "FastForestRegressionExec";
        public const string RegistrationName = "FastForestRegressionPredictor";

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "FFORE RE",
                // verWrittenCur: 0x00010001, Initial
                // verWrittenCur: 0x00010002, // InstanceWeights are part of QuantileRegression Tree to support weighted intances
                // verWrittenCur: 0x00010003, // _numFeatures serialized
                // verWrittenCur: 0x00010004, // Ini content out of predictor
                // verWrittenCur: 0x00010005, // Add _defaultValueForMissing
                verWrittenCur: 0x00010006, // Categorical splits.
                verReadableCur: 0x00010005,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        protected override uint VerNumFeaturesSerialized { get { return 0x00010003; } }

        protected override uint VerDefaultValueSerialized { get { return 0x00010005; } }

        protected override uint VerCategoricalSplitSerialized { get { return 0x00010006; } }

        internal FastForestRegressionPredictor(IHostEnvironment env, Ensemble trainedEnsemble, int featureCount,
            string innerArgs, int samplesCount)
            : base(env, RegistrationName, trainedEnsemble, featureCount, innerArgs)
        {
            _quantileSampleCount = samplesCount;
        }

        private FastForestRegressionPredictor(IHostEnvironment env, ModelLoadContext ctx)
            : base(env, RegistrationName, ctx, GetVersionInfo())
        {
            // *** Binary format ***
            // bool: should be always true
            // int: Quantile sample count
            Contracts.Check(ctx.Reader.ReadBoolByte());
            _quantileSampleCount = ctx.Reader.ReadInt32();
        }

        protected override void SaveCore(ModelSaveContext ctx)
        {
            base.SaveCore(ctx);
            ctx.SetVersionInfo(GetVersionInfo());

            // *** Binary format ***
            // bool: always true
            // int: Quantile sample count
            // Previously we store quantileEnabled parameter here,
            // but this paramater always should be true for regression.
            // If you update model version feel free to delete it.
            ctx.Writer.WriteBoolByte(true);
            ctx.Writer.Write(_quantileSampleCount);
        }

        public static FastForestRegressionPredictor Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            return new FastForestRegressionPredictor(env, ctx);
        }

        public override PredictionKind PredictionKind { get { return PredictionKind.Regression; } }

        protected override void Map(ref VBuffer<Float> src, ref Float dst)
        {
            if (InputType.VectorSize > 0)
                Host.Check(src.Length == InputType.VectorSize);
            else
                Host.Check(src.Length > MaxSplitFeatIdx);

            dst = (Float)TrainedEnsemble.GetOutput(ref src) / TrainedEnsemble.NumTrees;
        }

        public ValueMapper<VBuffer<Float>, VBuffer<Float>> GetMapper(Float[] quantiles)
        {
            return
                (ref VBuffer<Float> src, ref VBuffer<Float> dst) =>
                {
                    // REVIEW: Should make this more efficient - it repeatedly allocates too much stuff.
                    Float[] weights = null;
                    var distribution = TrainedEnsemble.GetDistribution(ref src, _quantileSampleCount, out weights);
                    var qdist = new QuantileStatistics(distribution, weights);

                    var values = dst.Values;
                    if (Utils.Size(values) < quantiles.Length)
                        values = new Float[quantiles.Length];
                    for (int i = 0; i < quantiles.Length; i++)
                        values[i] = qdist.GetQuantile((Float)quantiles[i]);
                    dst = new VBuffer<Float>(quantiles.Length, values, dst.Indices);
                };
        }

        public ISchemaBindableMapper CreateMapper(Double[] quantiles)
        {
            Host.CheckNonEmpty(quantiles, nameof(quantiles));
            return new SchemaBindableQuantileRegressionPredictor(this, quantiles);
        }
    }

    /// <include file='./doc.xml' path='docs/members/member[@name="FastForest"]/*' />
    public sealed partial class FastForestRegression : RandomForestTrainerBase<FastForestRegression.Arguments, FastForestRegressionPredictor>
    {
        public sealed class Arguments : FastForestArgumentsBase
        {
            [Argument(ArgumentType.LastOccurenceWins, HelpText = "Shuffle the labels on every iteration. " +
                "Useful probably only if using this tree as a tree leaf featurizer for multiclass.")]
            public bool ShuffleLabels;
        }

        internal const string Summary = "Trains a random forest to fit target values using least-squares.";

        internal const string LoadNameValue = "FastForestRegression";
        internal const string UserNameValue = "Fast Forest Regression";
        internal const string ShortName = "ffr";

        public FastForestRegression(IHostEnvironment env, Arguments args)
            : base(env, args, true)
        {
        }

        public override bool NeedCalibration
        {
            get { return false; }
        }

        public override PredictionKind PredictionKind { get { return PredictionKind.Regression; } }

        public override void Train(RoleMappedData trainData)
        {
            using (var ch = Host.Start("Training"))
            {
                ch.CheckValue(trainData, nameof(trainData));
                trainData.CheckRegressionLabel();
                trainData.CheckFeatureFloatVector();
                trainData.CheckOptFloatWeight();
                FeatureCount = trainData.Schema.Feature.Type.ValueCount;
                ConvertData(trainData);
                TrainCore(ch);
                ch.Done();
            }
        }

        public override FastForestRegressionPredictor CreatePredictor()
        {
            Host.Check(TrainedEnsemble != null,
                "The predictor cannot be created before training is complete");

            return new FastForestRegressionPredictor(Host, TrainedEnsemble, FeatureCount, InnerArgs, Args.QuantileSampleCount);
        }

        protected override void PrepareLabels(IChannel ch)
        {
        }

        protected override ObjectiveFunctionBase ConstructObjFunc(IChannel ch)
        {
            return ObjectiveFunctionImplBase.Create(TrainSet, Args);
        }

        protected override Test ConstructTestForTrainingData()
        {
            return new RegressionTest(ConstructScoreTracker(TrainSet));
        }

        private abstract class ObjectiveFunctionImplBase : RandomForestObjectiveFunction
        {
            private readonly float[] _labels;

            public static ObjectiveFunctionImplBase Create(Dataset trainData, Arguments args)
            {
                if (args.ShuffleLabels)
                    return new ShuffleImpl(trainData, args);
                return new BasicImpl(trainData, args);
            }

            private ObjectiveFunctionImplBase(Dataset trainData, Arguments args)
                : base(trainData, args, double.MaxValue) // No notion of maximum step size.
            {
                _labels = FastTreeRegressionTrainer.GetDatasetRegressionLabels(trainData);
                Contracts.Assert(_labels.Length == trainData.NumDocs);
            }

            protected override void GetGradientInOneQuery(int query, int threadIndex)
            {
                int begin = Dataset.Boundaries[query];
                int end = Dataset.Boundaries[query + 1];
                for (int i = begin; i < end; ++i)
                    Gradient[i] = _labels[i];
            }

            private sealed class ShuffleImpl : ObjectiveFunctionImplBase
            {
                private readonly Random _rgen;
                private readonly int _labelLim;

                public ShuffleImpl(Dataset trainData, Arguments args)
                    : base(trainData, args)
                {
                    Contracts.AssertValue(args);
                    Contracts.Assert(args.ShuffleLabels);

                    _rgen = new Random(0); // Ideally we'd get this from the host.

                    for (int i = 0; i < _labels.Length; ++i)
                    {
                        var lab = _labels[i];
                        if (!(0 <= lab && lab < Utils.ArrayMaxSize))
                        {
                            throw Contracts.ExceptUserArg(nameof(args.ShuffleLabels),
                                "Label {0} for example {1} outside of allowed range" +
                                "[0,{2}) when doing shuffled labels", lab, i, Utils.ArrayMaxSize);
                        }
                        int lim = (int)lab + 1;
                        Contracts.Assert(1 <= lim && lim <= Utils.ArrayMaxSize);
                        if (lim > _labelLim)
                            _labelLim = lim;
                    }
                }

                public override double[] GetGradient(IChannel ch, double[] scores)
                {
                    // Each time we get the gradient in random forest regression, it means
                    // we are building a new tree. Shuffle the targets!!
                    int[] map = Utils.GetRandomPermutation(_rgen, _labelLim);
                    for (int i = 0; i < _labels.Length; ++i)
                        _labels[i] = map[(int)_labels[i]];

                    return base.GetGradient(ch, scores);
                }
            }

            private sealed class BasicImpl : ObjectiveFunctionImplBase
            {
                public BasicImpl(Dataset trainData, Arguments args)
                    : base(trainData, args)
                {
                }
            }
        }
    }

    public static partial class FastForest
    {
        [TlcModule.EntryPoint(Name = "Trainers.FastForestRegressor",
            Desc = FastForestRegression.Summary,
            UserName = FastForestRegression.LoadNameValue,
            ShortName = FastForestRegression.ShortName,
            XmlInclude = new[] { @"<include file='../Microsoft.ML.FastTree/doc.xml' path='docs/members/member[@name=""FastForest""]/*' />" })]
        public static CommonOutputs.RegressionOutput TrainRegression(IHostEnvironment env, FastForestRegression.Arguments input)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register("TrainFastForest");
            host.CheckValue(input, nameof(input));
            EntryPointUtils.CheckInputArgs(host, input);

            return LearnerEntryPointsUtils.Train<FastForestRegression.Arguments, CommonOutputs.RegressionOutput>(host, input,
                () => new FastForestRegression(host, input),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.LabelColumn),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.WeightColumn),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.GroupIdColumn));
        }
    }
}
