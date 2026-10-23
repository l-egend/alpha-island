using System.Globalization;

namespace alpha_island.Core;

internal readonly record struct ScreenColor(byte Red,byte Green,byte Blue)
{
    internal string Hex=>$"#{Red:X2}{Green:X2}{Blue:X2}";
    internal string Rgb=>$"rgb({Red}, {Green}, {Blue})";
    internal string Hsl
    {
        get
        {
            double r=Red/255d,g=Green/255d,b=Blue/255d;
            double max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min;
            double light=(max+min)/2,saturation=delta==0?0:delta/(1-Math.Abs(2*light-1));
            double hue=delta==0?0:max==r?60*((g-b)/delta%6):max==g?60*((b-r)/delta+2):60*((r-g)/delta+4);
            if(hue<0)hue+=360;
            return string.Create(CultureInfo.InvariantCulture,$"hsl({hue:0.#}, {saturation*100:0.#}%, {light*100:0.#}%)");
        }
    }
}
