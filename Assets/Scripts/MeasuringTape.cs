using TMPro;
using UnityEngine;

public class MeasuringTape
{
    public GameObject TapeLine;
    public TextMeshPro TapeInfo;
    public static double MetersToInches(double meters) => meters * 39.3701;
    public static double MetersToCentimeters(double meters) => meters * 100;
}
