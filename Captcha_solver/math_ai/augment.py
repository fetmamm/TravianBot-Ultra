import cv2
import numpy as np

img = cv2.imread("debug_char_359.png", cv2.IMREAD_GRAYSCALE)

for i in range(20):
    dx = np.random.randint(-2, 2)
    dy = np.random.randint(-2, 2)

    M = np.float32([[1, 0, dx], [0, 1, dy]])
    shifted = cv2.warpAffine(img, M, (64, 64), borderValue=0)

    noise = np.random.randint(0, 15, (64, 64), dtype='uint8')
    noisy = cv2.add(shifted, noise)

    cv2.imwrite(f"dataset/3/aug_{i}.png", noisy)