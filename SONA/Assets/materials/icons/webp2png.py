import os
from PIL import Image

def ext_to_png(img_path):
    """Generates the new filename with .png extension."""
    path, _ = os.path.splitext(img_path)
    return f"{path}.png"

def img_conv(img_path):
    """Converts WEBP to PNG while preserving transparency."""
    try:
        png_file_path = ext_to_png(img_path)
        
        # Open the image
        with Image.open(img_path) as img:
            # Convert to RGBA to ensure the Alpha channel (transparency) is preserved
            # If the image is already RGBA, this ensures it stays that way
            # If it's Palette mode (P) with transparency, it converts it correctly
            png_version = img.convert("RGBA")
            
            # Save as PNG
            png_version.save(png_file_path, "PNG")
            print(f"Successfully converted: {os.path.basename(img_path)} -> {os.path.basename(png_file_path)}")
            
    except Exception as e:
        print(f"Failed to convert {img_path}: {e}")

if __name__ == "__main__":
    target_folder = r"D:\unity\NR\MJPEG server\Assets\icons"
    
    # Walk through the directory
    for foldername, subfolders, filenames in os.walk(target_folder):
        for filename in filenames:
            if filename.lower().endswith(".webp"):
                full_path = os.path.join(foldername, filename)
                img_conv(full_path)