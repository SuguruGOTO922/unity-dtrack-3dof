/* DTrackSDK in C#: MarkerParser.cs
 *
 * Parsing single 3DOF markers of DTRACK output data.
 *
 * Local extension of the ART DTRACK Plugin (not part of the upstream release),
 * distributed under the same BSD-3-Clause license as this package.
 *
 * Format: 3d <n> [<id> <qu>][<sx> <sy> <sz>] [<id> <qu>][<sx> <sy> <sz>] ...
 */

using System;
using System.Collections.Generic;
using System.Globalization;

namespace DTrackSDK.Parsers
{


public static class MarkerParser
{
	public static Dictionary< int, DTrackMarker > Parse( string raw, out int num )
	{
		string[] markerCountSplit = raw.Split( Statics.NumberSplit, 3 );
		int markerCount = Convert.ToInt32( markerCountSplit[ 1 ] );

		if ( markerCount <= 0 )
		{
			num = 0;
			return null;
		}

		var markers = new Dictionary< int, DTrackMarker >();

		string trimmed = markerCountSplit[ 2 ].Trim( Statics.SectionTrim );
		string[] sectionSplit = trimmed.Split( Statics.SectionSplit, StringSplitOptions.None );

		int iblk = 0;
		for ( int imarker = 0; imarker < markerCount; imarker++ )
		{
			string[] m = sectionSplit[ iblk++ ].Split( ' ' );
			string[] s = sectionSplit[ iblk++ ].Split( ' ' );

			int id = Convert.ToInt32( m[ 0 ] );
			float qu = Convert.ToSingle( m[ 1 ], CultureInfo.InvariantCulture );

			float sx = Convert.ToSingle( s[ 0 ], CultureInfo.InvariantCulture );
			float sy = Convert.ToSingle( s[ 1 ], CultureInfo.InvariantCulture );
			float sz = Convert.ToSingle( s[ 2 ], CultureInfo.InvariantCulture );

			markers[ id ] = new DTrackMarker( id, qu, sx, sy, sz );
		}

		num = markerCount;
		return markers;
	}
}


}  // namespace DTrackSDK.Parsers
