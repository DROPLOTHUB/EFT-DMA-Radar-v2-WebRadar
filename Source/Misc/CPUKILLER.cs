namespace eft_dma_radar.Source.Misc
{
    public class CPUKILLER
    {
        // Define EInteractionStatus struct
        public struct EInteractionStatus
        {
            public const uint TripwireInteractionSoundController = 0x330; // Offset for playing the tripwire sound
        }

        // Define the main FPSExploit class
        public static class FPSExploit
        {
            // Method to execute the exploit
            public static void ExecuteExploit()
            {
                // Check if the local player is the one interacting
         
                
                    // Trigger the tripwire interaction sound using the defined offset
                    // Write '1' to the offset to enable the sound
                    Memory.WriteValue(EInteractionStatus.TripwireInteractionSoundController, 1); // 1 to trigger the sound
                
            }

        
        }
    }
}
