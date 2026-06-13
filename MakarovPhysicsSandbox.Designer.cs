namespace MakarovPhysicsSandbox
{
    partial class MakarovPhysicsSandbox
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
                components.Dispose();
            base.Dispose(disposing);
        }

        // The UI (GL panel, toolbar, menu, status bar) is built in code in BuildUi(),
        // so the designer only sets up the form shell. Keeps things readable and avoids
        // storing toolbar images in the .resx.
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            SuspendLayout();
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1280, 820);
            MinimumSize = new Size(640, 480);
            Name = "MakarovPhysicsSandbox";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Makarov Physics Sandbox";
            ResumeLayout(false);
        }
    }
}
