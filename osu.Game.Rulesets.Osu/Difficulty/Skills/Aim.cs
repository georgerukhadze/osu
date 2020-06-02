﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics;
using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.RootFinding;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Difficulty.MathUtil;

namespace osu.Game.Rulesets.Osu.Difficulty.Skills
{
    /// <summary>
    /// Represents the skill required to correctly aim at every object in the map with a uniform CircleSize and normalized distances.
    /// </summary>
    public class Aim : Skill
    {
        /// <summary>
        /// We want to find a throughput level at which the probability of FC = prob_threshold
        /// </summary>
        private const double prob_threshold = 0.02;

        /// <summary>
        /// We want to find a throughput level at which (the expected time for FC - the length of the song) = time_threshold_base
        /// </summary>
        private const double time_threshold_base = 1200;

        /// <summary>
        /// Minimum throughput for root-finding
        /// </summary>
        private const double tp_min = 0.1;

        /// <summary>
        /// Maximum throughput for root-finding
        /// </summary>
        private const double tp_max = 100;

        /// <summary>
        /// Precision of probability of FC for root-finding
        /// </summary>
        private const double prob_precision = 1e-4;

        /// <summary>
        /// Precision of expected time for FC for root-finding
        /// </summary>
        private const double time_precision = 0.6;

        /// <summary>
        /// Maximum number of iterations for root-finding
        /// </summary>
        private const int max_iterations = 100;

        private const double default_cheese_level = 0.4;
        private const int cheese_level_count = 11;

        private const int miss_tp_count = 20;
        private const int combo_tp_count = 50;

        /// <summary>
        /// Calculates attributes related to aiming difficulty.
        /// </summary>
        public static (double, double, double[], double[], double[], double, double[], double[])
            CalculateAimAttributes(List<OsuHitObject> hitObjects,
                                   double clockRate,
                                   List<Vector<double>> strainHistory,
                                   List<double> noteDensities)
        {
            List<OsuMovement> movements = createMovements(hitObjects, clockRate, strainHistory);
            List<OsuMovement> movementsHidden = createMovements(hitObjects, clockRate, strainHistory,
                hidden: true, noteDensities: noteDensities);

            var mapHitProbs = new HitProbabilities(movements, default_cheese_level, difficultyCount: combo_tp_count);
            double fcProbTp = calculateFcProbTp(movements);
            double fcProbTpHidden = calculateFcProbTp(movementsHidden);

            double hiddenFactor = fcProbTpHidden / fcProbTp;

            double[] comboTps = calculateComboTps(mapHitProbs);
            double fcTimeTp = comboTps.Last();
            var (missTps, missCounts) = calculateMissTpsMissCounts(movements, fcTimeTp);
            var (cheeseLevels, cheeseFactors) = calculateCheeseLevelsCheeseFactors(movements, fcProbTp);
            double cheeseNoteCount = getCheeseNoteCount(movements, fcProbTp);

            return (fcProbTp, hiddenFactor, comboTps, missTps, missCounts, cheeseNoteCount, cheeseLevels, cheeseFactors);
        }

        /// <summary>
        /// Converts hit objects into movements.
        /// </summary>
        private static List<OsuMovement> createMovements(List<OsuHitObject> hitObjects, double clockRate, List<Vector<double>> strainHistory,
                                                         bool hidden = false, List<double> noteDensities = null)
        {
            OsuMovement.Initialize();
            var movements = new List<OsuMovement>();

            if (hitObjects.Count == 0)
                return movements;

            // the first object
            movements.AddRange(OsuMovement.ExtractMovement(hitObjects[0]));

            // the rest
            for (int i = 1; i < hitObjects.Count; i++)
            {
                var objMinus2 = i > 3 ? hitObjects[i - 4] : null;
                var obj0 = i > 1 ? hitObjects[i - 2] : null;
                var obj1 = hitObjects[i - 1];
                var obj2 = hitObjects[i];
                var obj3 = i < hitObjects.Count - 1 ? hitObjects[i + 1] : null;
                var tapStrain = strainHistory[i];

                if (hidden)
                {
                    movements.AddRange(OsuMovement.ExtractMovement(obj0, obj1, obj2, obj3, tapStrain, clockRate,
                        hidden: true, noteDensity: noteDensities[i], objMinus2: objMinus2));
                }
                else
                    movements.AddRange(OsuMovement.ExtractMovement(obj0, obj1, obj2, obj3, tapStrain, clockRate, objMinus2: objMinus2));
            }

            return movements;
        }

        /// <summary>
        /// Calculates the throughput at which the probability of FC = prob_threshold
        /// </summary>
        private static double calculateFcProbTp(IEnumerable<OsuMovement> movements, double cheeseLevel = default_cheese_level)
        {
            double fcProbTpMin = calculateFcProb(movements, tp_min, cheeseLevel);

            if (fcProbTpMin >= prob_threshold)
                return tp_min;

            double fcProbTpMax = calculateFcProb(movements, tp_max, cheeseLevel);

            if (fcProbTpMax <= prob_threshold)
                return tp_max;

            double fcProbMinusThreshold(double tp) => calculateFcProb(movements, tp, cheeseLevel) - prob_threshold;
            return Brent.FindRoot(fcProbMinusThreshold, tp_min, tp_max, prob_precision, max_iterations);
        }

        /// <summary>
        /// Calculates the throughput at which MinExpectedTimeForCount(throughput, sectionCount) = timeThresholdBase.
        /// </summary>
        // The map is divided into combo_tp_count sections, and a submap can span x sections.
        // This function calculates the minimum skill level such that
        // there exists a submap of length sectionCount that can be FC'd in timeThresholdBase seconds.
        private static double calculateFcTimeTp(HitProbabilities mapHitProbs, int sectionCount)
        {
            if (mapHitProbs.IsEmpty(sectionCount))
                return 0;

            double maxFcTime = mapHitProbs.MinExpectedTimeForCount(tp_min, sectionCount);

            if (maxFcTime <= time_threshold_base)
                return tp_min;

            double minFcTime = mapHitProbs.MinExpectedTimeForCount(tp_max, sectionCount);

            if (minFcTime >= time_threshold_base)
                return tp_max;

            double fcTimeMinusThreshold(double tp) => mapHitProbs.MinExpectedTimeForCount(tp, sectionCount) - time_threshold_base;
            return Bisection.FindRoot(fcTimeMinusThreshold, tp_min, tp_max, time_precision, max_iterations);
        }

        /// <summary>
        /// Calculate miss count for a list of throughputs (used to evaluate miss count of plays).
        /// </summary>
        private static (double[], double[]) calculateMissTpsMissCounts(IList<OsuMovement> movements, double fcTimeTp)
        {
            double[] missTps = new double[miss_tp_count];
            double[] missCounts = new double[miss_tp_count];
            double fcProb = calculateFcProb(movements, fcTimeTp, default_cheese_level);

            for (int i = 0; i < miss_tp_count; i++)
            {
                double missTp = fcTimeTp * (1 - Math.Pow(i, 1.5) * 0.005);
                double[] missProbs = getMissProbs(movements, missTp);
                missTps[i] = missTp;
                missCounts[i] = getMissCount(fcProb, missProbs);
            }

            return (missTps, missCounts);
        }

        /// <summary>
        /// Calculate the probability of missing each note given a skill level.
        /// </summary>
        private static double[] getMissProbs(IList<OsuMovement> movements, double tp)
        {
            // slider breaks should be a miss :( -- joz, 2019
            var missProbs = new double[movements.Count];

            for (int i = 0; i < movements.Count; ++i)
            {
                var movement = movements[i];
                missProbs[i] = 1 - HitProbabilities.CalculateCheeseHitProb(movement, tp, default_cheese_level);
            }

            return missProbs;
        }

        /// <summary>
        /// Find first miss count achievable with at least probability p
        /// </summary>
        private static double getMissCount(double p, double[] missProbabilities)
        {
            if (missProbabilities.Sum() == 0)
                return 0;

            var distribution = new PoissonBinomial(missProbabilities);

            double cdfMinusProb(double missCount) => distribution.Cdf(missCount) - p;
            return Brent.FindRootExpand(cdfMinusProb, -100, 1000);
        }

        /// <summary>
        /// For each cheese level, it first calculates the required throughput,
        /// then divides the result by the throughput corresponding to the default cheese level.
        /// </summary>
        private static (double[], double[]) calculateCheeseLevelsCheeseFactors(IList<OsuMovement> movements, double fcProbTp)
        {
            double[] cheeseLevels = new double[cheese_level_count];
            double[] cheeseFactors = new double[cheese_level_count];

            for (int i = 0; i < cheese_level_count; i++)
            {
                double cheeseLevel = (double)i / (cheese_level_count - 1);
                cheeseLevels[i] = cheeseLevel;
                cheeseFactors[i] = calculateFcProbTp(movements, cheeseLevel) / fcProbTp;
            }

            return (cheeseLevels, cheeseFactors);
        }

        /// <summary>
        /// Gets the number of movements that might be cheesed.
        /// A movement might be cheesed if it is both difficult and cheesable.
        /// </summary>
        private static double getCheeseNoteCount(IList<OsuMovement> movements, double tp)
        {
            double count = 0;

            foreach (var movement in movements)
            {
                double cheeseness = SpecialFunctions.Logistic((movement.Ip12 / tp - 0.6) * 15) * movement.Cheesablility;
                count += cheeseness;
            }

            return count;
        }

        /// <summary>
        /// The map is divided into combo_tp_count sections, and a submap can span x sections.
        /// This function calculates fcTimeTp for every possible submap length.
        /// </summary>
        private static double[] calculateComboTps(HitProbabilities hitProbabilities)
        {
            double[] comboTps = new double[combo_tp_count];

            for (int i = 1; i <= combo_tp_count; ++i)
            {
                comboTps[i - 1] = calculateFcTimeTp(hitProbabilities, i);
            }

            return comboTps;
        }

        /// <summary>
        /// Calculates the probability to FC the movements.
        /// </summary>
        private static double calculateFcProb(IEnumerable<OsuMovement> movements, double tp, double cheeseLevel)
        {
            double fcProb = 1;

            foreach (OsuMovement movement in movements)
            {
                double hitProb = HitProbabilities.CalculateCheeseHitProb(movement, tp, cheeseLevel);
                fcProb *= hitProb;
            }

            return fcProb;
        }

        protected override double SkillMultiplier => 0;
        protected override double StrainDecayBase => 0;

        protected override double StrainValueOf(DifficultyHitObject current)
        {
            throw new NotImplementedException();
        }
    }
}
