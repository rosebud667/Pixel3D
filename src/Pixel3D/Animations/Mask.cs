﻿using System;
using Microsoft.Xna.Framework;
using System.Diagnostics;
using Pixel3D.Extensions;

namespace Pixel3D.Animations
{
    public class Mask
    {
        public Mask()
        {
            // Blank default constructor because there's another constructor for deserialization
        }


        public MaskData data;


        /// <summary>True if this mask is auto-generated based on the alpha-channel of a <see cref="Cel"/> or <see cref="AnimationFrame"/>.</summary>
        /// <remak>It is not permissable to use an alpha mask for any other purpose (this greatly simplifies code for tracking and regenerating alpha masks).</remak>
        public bool isGeneratedAlphaMask;


        #region Editor Stuff

        /// <summary>Does nothing.</summary>
        [Obsolete]
        public string friendlyName { get { return string.Empty; } set { } }

        public Mask Clone()
        {
            Mask clone = new Mask();
            clone.data = data.Clone();
            clone.isGeneratedAlphaMask = isGeneratedAlphaMask;
            return clone;
        }

        #endregion


        public Rectangle Bounds { get { return data.Bounds; } }



        /// <summary>Return mask data that has been transformed by this mask view and the supplied transformation</summary>
        public TransformedMaskData GetTransformedMaskData(Point transformPosition, bool transformFlipX)
        {
            if(transformFlipX)
                transformPosition.X = -transformPosition.X; // Flip this so that it gets unflipped when resulting mask data gets flipped

            TransformedMaskData result;
            result.flipX = transformFlipX;
            result.maskData = data.Translated(transformPosition);

            return result;
        }

        /// <summary>Return mask data that has been transformed by this mask view and the supplied transformation</summary>
        public TransformedMaskData GetTransformedMaskData(Position transformPosition, bool transformFlipX)
        {
            return GetTransformedMaskData(transformPosition.ToWorldZero(), transformFlipX);
        }

        /// <summary>Return mask data that has been transformed by this mask view (simple 2D transform of the data) and then transformed as an XZ mask on an actor</summary>
        public TransformedMaskData GetTransformedMaskDataXZ(Position transformPosition, bool transformFlipX)
        {
            // NOTE: Don't take into account Y axis. This is safe, because there is a direct conversion from Y to Z in world space.
            return GetTransformedMaskData(new Position(transformPosition.X, 0, transformPosition.Z), transformFlipX);
        }
    }
}
