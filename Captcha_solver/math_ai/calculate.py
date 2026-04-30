import sympy as sp

expression = "2+2"

expression = expression.replace("plus", "+")
expression = expression.replace("minus", "-")

answer = sp.sympify(expression)

print("Expression:", expression)
print("Answer:", answer)