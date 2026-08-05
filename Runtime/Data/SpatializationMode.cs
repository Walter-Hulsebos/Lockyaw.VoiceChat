using UnityEngine;

namespace Lockyaw.VoiceChat {

    public enum SpatializationMode {
        [InspectorName("2D Audio")]
        TwoDimensional = 0,
        [InspectorName("Unity 3D Audio")]
        UnityThreeDimensional = 1,
        [InspectorName("Project Spatializer")]
        ProjectSpatializer = 2
    }

}
