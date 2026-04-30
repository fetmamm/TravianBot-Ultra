import cv2
import numpy as np
import os
import random

input_dir = "dataset/0"
output_dir = "dataset/0_aug"

os.makedirs(output_dir, exist_ok=True)

def augment(img):
    rows, cols = img.shape

    # slumpmässig shift
    tx = random.randint(-4, 4)
    ty = random.randint(-4, 4)
    M = np.float32([[1, 0, tx], [0, 1, ty]])
    img = cv2.warpAffine(img, M, (cols, rows), borderValue=0)

    # slumpmässig rotation
    angle = random.uniform(-10, 10)
    M = cv2.getRotationMatrix2D((cols//2, rows//2), angle, 1)
    img = cv2.warpAffine(img, M, (cols, rows), borderValue=0)

    # lite brus
    noise = np.random.randint(0, 20, (rows, cols), dtype='uint8')
    img = cv2.add(img, noise)

    return img

count = 0

for filename in os.listdir(input_dir):
    if not filename.endswith(".png"):
        continue

    path = os.path.join(input_dir, filename)
    img = cv2.imread(path, cv2.IMREAD_GRAYSCALE)

    if img is None:
        continue

    for i in range(20):  # 20 nya per bild
        aug = augment(img)
        save_path = os.path.join(output_dir, f"{filename}_{i}.png")
        cv2.imwrite(save_path, aug)
        count += 1

print("Generated:", count, "images")